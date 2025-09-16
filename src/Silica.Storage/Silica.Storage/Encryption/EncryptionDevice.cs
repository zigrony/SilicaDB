using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Decorators;
using Silica.Storage.Encryption.Exceptions;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Storage.Encryption.Metrics;
using Silica.Storage.Encryption.Diagnostics;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Generic;

namespace Silica.Storage.Encryption
{
    // Passive, frame-preserving encryption wrapper using AES-CTR.
    // Confidentiality only (no MAC). Exact frame size preserved.
    public sealed class EncryptionDevice : StorageDeviceDecorator, IStackManifestHost
    {
        static EncryptionDevice()
        {
            // Ensure encryption exceptions are registered once per process.
            try { EncryptionExceptions.RegisterAll(); } catch { }
        }

        private readonly IEncryptionKeyProvider _keys;
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;
        private readonly object _metricsLock = new object();
        private readonly int _keyLen;
        private readonly int _saltLen;
        // Counter derivation config and strategy
        private readonly CounterDerivationConfig _cfg;
        private readonly ICounterDerivation _derivation;

        // Back-compat ctor: defaults to deterministic salt^frameId (no epoch)
        public EncryptionDevice(IStorageDevice inner, IEncryptionKeyProvider keys)
            : this(inner, keys, new CounterDerivationConfig(CounterDerivationKind.SaltXorFrameId_V1, 1, 0, 0))
        {
            // Warn explicitly: CTR unauthenticated + keystream reuse on rewrites with this default
            try
            {
                EncryptionDiagnostics.Emit("init", "warn", "aes_ctr_unauthenticated_and_nonce_reuse_on_rewrites",
                    more: new Dictionary<string, string>
                    {
                        { "mode", "AES-CTR" },
                        { "integrity", "none" },
                        { "frame_keystream_reuse", "on_rewrite" }
                    });
            }
            catch { }
        }

        // Preferred ctor: explicit, versioned counter derivation strategy
        public EncryptionDevice(IStorageDevice inner, IEncryptionKeyProvider keys, CounterDerivationConfig cfg)
            : base(inner)
        {
            _keys = keys ?? throw new EncryptionKeyProviderNullException();
            var key = _keys.GetKey();
            int keyLen = key.Length;
            if (keyLen != 16 && keyLen != 24 && keyLen != 32)
            {
                try
                {
                    EncryptionDiagnostics.Emit("init", "error", "invalid_key_length",
                        more: new Dictionary<string, string> { { "key_bytes", keyLen.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
                IncrementValidationFailureMetric();
                throw new InvalidKeyException(key.Length);
            }
            var salt = _keys.GetDeviceSalt();
            int saltLen = salt.Length;
            if (saltLen != 16)
            {
                try
                {
                    EncryptionDiagnostics.Emit("init", "error", "invalid_salt_length",
                        more: new Dictionary<string, string> { { "salt_bytes", saltLen.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
                IncrementValidationFailureMetric();
                throw new InvalidSaltException(saltLen);
            }

            _keyLen = keyLen;
            _saltLen = saltLen;
            _componentName = GetType().Name;
            _metrics = DiagnosticsCoreBootstrap.IsStarted
                ? DiagnosticsCoreBootstrap.Instance.Metrics
                : new NoOpMetricsManager();

            // Bind counter derivation strategy
            _cfg = cfg;
            switch (_cfg.Kind)
            {
                case CounterDerivationKind.SaltXorFrameId_V1:
                    _derivation = SaltXorFrameIdV1.Instance;
                    break;
                case CounterDerivationKind.SaltXorFrameId_Epoch_V1:
                    _derivation = new SaltXorFrameIdEpochV1(_cfg.Epoch);
                    break;
                default:
                    // Future strategies can be added; default to deterministic V1 if unknown
                    _derivation = SaltXorFrameIdV1.Instance;
                    break;
            }

            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                try
                {
                    EncryptionMetrics.RegisterAll(_metrics, _componentName);
                    _metricsRegistered = true;
                    EncryptionDiagnostics.Emit("init", "info", "encryption_device_initialized",
                         more: new Dictionary<string, string>
                         {
                            { "component", _componentName },
                            { "key_bytes", keyLen.ToString(CultureInfo.InvariantCulture) },
                            { "salt_bytes", "16" },
                            { "ctr_kind", ((uint)_cfg.Kind).ToString(CultureInfo.InvariantCulture) },
                            { "ctr_ver", _cfg.Version.ToString(CultureInfo.InvariantCulture) },
                            { "ctr_epoch", _cfg.Epoch.ToString(CultureInfo.InvariantCulture) }
                         });
                }
                catch { }
            }
        }

        private void EnsureMetricsRegistered()
        {
            if (_metricsRegistered) return;
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            lock (_metricsLock)
            {
                if (_metricsRegistered) return;
                _metrics = DiagnosticsCoreBootstrap.Instance.Metrics;
                try
                {
                    EncryptionMetrics.RegisterAll(_metrics, _componentName);
                    _metricsRegistered = true;
                }
                catch { }
            }
        }

        private void IncrementValidationFailureMetric()
        {
            try { EncryptionMetrics.IncrementValidationFailure(_metrics ?? new NoOpMetricsManager()); } catch { }
        }

        // Synchronous helper: constructs counter with stackalloc and decrypts in place.
        private void DecryptInPlace(long frameId, Memory<byte> buffer, int count)
        {
            if (count <= 0) return;

            // Enforce provider invariants on every use.
            var keySpan = _keys.GetKey();
            if (keySpan.Length != _keyLen)
            {
                try { EncryptionMetrics.IncrementValidationFailure(_metrics); } catch { }
                throw new InvalidKeyException(keySpan.Length);
            }

            Span<byte> counter = stackalloc byte[16];
            InitCounterBlock(frameId, counter);
            using (var ctr = new AesCtrTransform(keySpan, counter))
            {
                var dst = buffer.Span.Slice(0, count);
                ctr.Transform(dst, dst); // CTR decrypt == encrypt
            }
        }

        // Synchronous helper: constructs counter and encrypts src into provided destination buffer (in place if same).
        private void EncryptIntoBuffer(long frameId, ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (src.Length != dst.Length) throw new MismatchedBufferLengthException(src.Length, dst.Length);
            // Copy plaintext into destination, supporting in-place or overlapping spans safely.
            src.CopyTo(dst);

            // Enforce provider invariants on every use.
            var keySpan = _keys.GetKey();
            if (keySpan.Length != _keyLen)
            {
                try { EncryptionMetrics.IncrementValidationFailure(_metrics); } catch { }
                throw new InvalidKeyException(keySpan.Length);
            }

            Span<byte> counter = stackalloc byte[16];
            InitCounterBlock(frameId, counter);
            using (var ctr = new AesCtrTransform(keySpan, counter))
            {
                ctr.Transform(dst, dst);
            }
        }

        // Derive a 16-byte counter block for AES-CTR using the configured derivation strategy.
        private void InitCounterBlock(long frameId, Span<byte> counter16)
        {
            if (counter16.Length != 16)
                throw new InvalidCounterBlockException(counter16.Length);

            var salt = _keys.GetDeviceSalt();
            if (salt.Length != _saltLen || salt.Length != 16)
            {
                try { EncryptionMetrics.IncrementValidationFailure(_metrics); } catch { }
                throw new InvalidSaltException(salt.Length);
            }
            _derivation.Derive(salt, frameId, counter16);
        }

        // READ: split-path so no Span/stackalloc lives in an async body
        public override ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
        {
            EnsureMetricsRegistered();
            var sw = Stopwatch.StartNew();
            if (EncryptionDiagnostics.EnableFrameInfoLogs)
            {
                try
                {
                    EncryptionDiagnostics.Emit("read_frame", "debug", "begin",
                        more: new Dictionary<string, string> { { "frame", frameId.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
            }

            ValueTask<int> pending;
            try
            {
                pending = base.ReadFrameAsync(frameId, buffer, token);
            }
            catch (Exception ex)
            {
                // Synchronous inner failure: close out timing and emit a precise diagnostic
                sw.Stop();
                // Intentionally not incrementing transform-failure metrics: this is an inner IO failure,
                // not a crypto transform failure by taxonomy.
                try
                {
                    EncryptionDiagnostics.Emit("read_frame", "error", "inner_read_failed", ex,
                        more: new Dictionary<string, string>
                        {
                            { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                            { "bytes", buffer.Length.ToString(CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
                throw;
            }

            if (pending.IsCompletedSuccessfully)
            {
                int n = pending.Result;
                try
                {
                    if (n > 0) DecryptInPlace(frameId, buffer, n);
                    return ValueTask.FromResult(n);
                }
                catch (TransformFailedException)
                {
                    try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                    throw new TransformFailedException("decrypt", ex);
                }
                finally
                {
                    sw.Stop();
                    try { EncryptionMetrics.OnDecryptCompleted(_metrics, sw.Elapsed.TotalMilliseconds, n); } catch { }
                    if (EncryptionDiagnostics.EnableFrameInfoLogs)
                    {
                        try
                        {
                            EncryptionDiagnostics.Emit("read_frame", "info", "done",
                                more: new Dictionary<string, string>
                                {
                                    { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                                    { "bytes", n.ToString(CultureInfo.InvariantCulture) },
                                    { "ms", sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) }
                                });
                        }
                        catch { }
                    }
                }
            }
            return AwaitAndDecryptAsync(pending, frameId, buffer, sw);
        }

        // Async helper has NO stackalloc/Span locals; it awaits the inner read first
        // to preserve I/O vs. transform taxonomy symmetry with the synchronous fast-path.
        private async ValueTask<int> AwaitAndDecryptAsync(
            ValueTask<int> pending,
            long frameId,
            Memory<byte> buffer,
            Stopwatch sw)
        {
            int n;
            try
            {
                n = await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Do not collapse storage/I-O failures into transform taxonomy.
                sw.Stop();
                try
                {
                    EncryptionDiagnostics.Emit("read_frame", "error", "inner_read_failed", ex,
                        more: new Dictionary<string, string>
                        {
                            { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                            { "bytes", buffer.Length.ToString(CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
                throw;
            }

            try
            {
                if (n > 0) DecryptInPlace(frameId, buffer, n);
                return n;
            }
            catch (TransformFailedException)
            {
                try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                throw new TransformFailedException("decrypt", ex);
            }
            finally
            {
                sw.Stop();
                try { EncryptionMetrics.OnDecryptCompleted(_metrics, sw.Elapsed.TotalMilliseconds, n); } catch { }
                if (EncryptionDiagnostics.EnableFrameInfoLogs)
                {
                    try
                    {
                        EncryptionDiagnostics.Emit("read_frame", "info", "done",
                            more: new Dictionary<string, string>
                            {
                                { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                                { "bytes", n.ToString(CultureInfo.InvariantCulture) },
                                { "ms", sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) }
                            });
                    }
                    catch { }
                }
            }
        }

        // WRITE: encrypt synchronously, then forward write. No async/await here.
        public override ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (data.Length != Geometry.LogicalBlockSize)
            {
                try { EnsureMetricsRegistered(); EncryptionMetrics.IncrementValidationFailure(_metrics); } catch { }
                throw new InvalidFrameLengthException(data.Length, Geometry.LogicalBlockSize);
            }

            EnsureMetricsRegistered();
            var sw = Stopwatch.StartNew();
            if (EncryptionDiagnostics.EnableFrameInfoLogs)
            {
                try
                {
                    EncryptionDiagnostics.Emit("write_frame", "debug", "begin",
                        more: new Dictionary<string, string>
                        {
                            { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                            { "bytes", data.Length.ToString(CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
            }
            // Encrypt into a pooled buffer to avoid per-call allocations
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            byte[] rented = pool.Rent(data.Length);
            try
            {
                EncryptIntoBuffer(frameId, data.Span, rented.AsSpan(0, data.Length));
                // Honor cancellation after expensive encryption, before touching the inner device
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                // Preserve cancellation taxonomy; ensure the pool buffer is returned.
                try { pool.Return(rented, clearArray: true); } catch { }
                throw;
            }
            catch (TransformFailedException)
            {
                try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                pool.Return(rented, clearArray: true);
                throw;
            }
            catch (Exception ex)
            {
                try { EncryptionMetrics.IncrementTransformFailure(_metrics); } catch { }
                pool.Return(rented, clearArray: true);
                throw new TransformFailedException("encrypt", ex);
            }
            try
            {
                var vt = base.WriteFrameAsync(frameId, new ReadOnlyMemory<byte>(rented, 0, data.Length), token);
                if (vt.IsCompletedSuccessfully)
                {
                    try
                    {
                        sw.Stop();
                        return ValueTask.CompletedTask;
                    }
                    finally
                    {
                        try { EncryptionMetrics.OnEncryptCompleted(_metrics, sw.Elapsed.TotalMilliseconds, data.Length); } catch { }
                        if (EncryptionDiagnostics.EnableFrameInfoLogs)
                        {
                            try
                            {
                                EncryptionDiagnostics.Emit("write_frame", "info", "done",
                                    more: new Dictionary<string, string>
                                    {
                                        { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                                        { "bytes", data.Length.ToString(CultureInfo.InvariantCulture) },
                                        { "ms", sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) }
                                    });
                            }
                            catch { }
                        }
                        pool.Return(rented, clearArray: true);
                    }
                }
                return AwaitAndRecordEncryptAsync(vt, sw, frameId, data.Length, rented, pool);
            }
            catch
            {
                // Ensure the rented buffer is returned on any synchronous failure from the inner write
                try { pool.Return(rented, clearArray: true); } catch { }
                throw;
            }
        }

        // Prevent cleartext bypass via offset/length APIs. This device is frame-preserving encryption only.
        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
        {
            // Contract-first explicit refusal: callers must use frame-granular APIs on the encryption layer.
            // Rationale: offset/length transforms require authenticated RMW; not supported in this device.
            throw new Silica.Storage.Exceptions.UnsupportedOperationException("EncryptedDevice.OffsetLengthIoNotSupported");
        }

        public override ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            // Contract-first explicit refusal: callers must use frame-granular APIs on the encryption layer.
            throw new Silica.Storage.Exceptions.UnsupportedOperationException("EncryptedDevice.OffsetLengthIoNotSupported");
        }

        private async ValueTask AwaitAndRecordEncryptAsync(ValueTask pending, Stopwatch sw, long frameId, int bytes, byte[] rented, System.Buffers.ArrayPool<byte> pool)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                try { EncryptionMetrics.OnEncryptCompleted(_metrics, sw.Elapsed.TotalMilliseconds, bytes); } catch { }
                if (EncryptionDiagnostics.EnableFrameInfoLogs)
                {
                    try
                    {
                        EncryptionDiagnostics.Emit("write_frame", "info", "done",
                            more: new Dictionary<string, string>
                            {
                                { "frame", frameId.ToString(CultureInfo.InvariantCulture) },
                                { "bytes", bytes.ToString(CultureInfo.InvariantCulture) },
                                { "ms", sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) }
                            });
                    }
                    catch { }
                }
                try { pool.Return(rented, clearArray: true); } catch { }
            }
        }
    }
}
