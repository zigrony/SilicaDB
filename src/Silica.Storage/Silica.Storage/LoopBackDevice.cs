// Filename: \Silica.Storage\Silica.Storage\LoopbackDevice.cs
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Silica.Storage.Interfaces;
using Silica.Storage.Exceptions;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.Storage.Metrics; // StorageMetrics

namespace Silica.Storage
{
    public sealed class LoopbackDevice : IStorageDevice, IMountableStorage
    {
        private readonly ConcurrentDictionary<long, byte[]> _frames = new();
        private readonly int _frameSize;
        private readonly byte[] _zeroFrame;
        private long _maxFrameWritten = -1; // -1 => empty device
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private readonly StorageMetrics.IInFlightProvider _inFlightGauge;
        private bool _metricsRegistered;
        private readonly object _stateLock = new();
        private bool _mounted;
        private int _disposedFlag; // 0 = not disposed, 1 = disposed


        // Common defaults: no IO cap, no alignment requirement, no FUA
        public LoopbackDevice(
            int frameSize,
            int logicalBlockSize = 4096,
            int maxIoBytes = 0,
            bool requiresAlignedIo = false,
            bool supportsFua = false)
        {
            if (frameSize <= 0) throw new InvalidLengthException(frameSize);
            _frameSize = frameSize;
            _zeroFrame = new byte[_frameSize];
            // Enforce contract: frame I/O size must equal logical block size.
            if (logicalBlockSize != frameSize)
                throw new InvalidGeometryException("logicalBlockSize must equal frameSize for LoopbackDevice.");
            if (maxIoBytes < 0) throw new InvalidLengthException(maxIoBytes);
            if (maxIoBytes > 0 && (maxIoBytes % logicalBlockSize) != 0)
                throw new InvalidGeometryException("maxIoBytes must be a multiple of logicalBlockSize when > 0.");


            Geometry = new StorageGeometry
            {
                LogicalBlockSize = frameSize,
                MaxIoBytes = maxIoBytes,           // 0 => no artificial cap
                RequiresAlignedIo = requiresAlignedIo,
                SupportsFua = supportsFua
            };

            // DiagnosticsCore metrics wiring
            _componentName = GetType().Name;
            _metrics = DiagnosticsCoreBootstrap.IsStarted ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager();
            _inFlightGauge = StorageMetrics.CreateInFlightCounter();

            // Register Storage metrics contract for this device
            try
            {
                StorageMetrics.RegisterAll(
                    _metrics,
                    deviceComponentName: _componentName,
                    frameLockCacheSizeProvider: null,
                    freeSpaceProvider: null,
                    queueDepthProvider: null,
                    inFlightProvider: _inFlightGauge);
                _metricsRegistered = DiagnosticsCoreBootstrap.IsStarted;
            }
            catch { /* swallow exporter/registration issues */ }

        }
        private void EnsureMetricsRegistered()
        {
            if (_metricsRegistered) return;
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            lock (_stateLock)
            {
                if (_metricsRegistered) return;
                _metrics = DiagnosticsCoreBootstrap.Instance.Metrics;
                try
                {
                    StorageMetrics.RegisterAll(
                        _metrics,
                        deviceComponentName: _componentName,
                        frameLockCacheSizeProvider: null,
                        freeSpaceProvider: null,
                        queueDepthProvider: null,
                        inFlightProvider: _inFlightGauge);
                    _metricsRegistered = true;
                }
                catch { /* swallow */ }
            }
        }

        public StorageGeometry Geometry { get; }

        public ValueTask FlushAsync(CancellationToken token = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // Idempotent; mark disposed and unmounted, clear frames for hygiene
            if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) == 1)
                return ValueTask.CompletedTask;
            lock (_stateLock) { _mounted = false; }
            try { _frames.Clear(); } catch { }
            return ValueTask.CompletedTask;
        }

        // --- IMountableStorage (no-op) ---
        public Task MountAsync(CancellationToken cancellationToken = default)
        {
            if (System.Threading.Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();
            lock (_stateLock) { _mounted = true; }
            return Task.CompletedTask;
        }
        public Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock) { _mounted = false; }
            return Task.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureMounted()
        {
            // Prefer disposed signal over "not mounted"
            if (System.Threading.Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();

            lock (_stateLock)
            {
                if (!_mounted) throw new DeviceNotMountedException();
            }
        }


        // -------- Frame-granular I/O --------

        public ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            token.ThrowIfCancellationRequested();
            if (frameId < 0) throw new InvalidOffsetException(frameId);
            if (buffer.Length != _frameSize)
                throw new InvalidLengthException(buffer.Length);

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int toCopy = _frameSize;
            var span = buffer.Span;
            if (_frames.TryGetValue(frameId, out var existing))
            {
                existing.AsSpan(0, toCopy).CopyTo(span);
                sw.Stop();
                try
                {
                    StorageMetrics.IncrementCacheHit(_metrics);
                    StorageMetrics.OnReadCompleted(_metrics, sw.Elapsed.TotalMilliseconds, toCopy);
                }
                catch { }
                finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
                return ValueTask.FromResult(toCopy);
            }
            try { StorageMetrics.IncrementCacheMiss(_metrics); } catch { }
            sw.Stop();
            if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } }
            var devLen = GetDeviceLength();
            throw new DeviceReadOutOfRangeException(
                offset: frameId * (long)_frameSize,
                requestedLength: _frameSize,
                deviceLength: devLen);
        }

        public ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            token.ThrowIfCancellationRequested();
            if (frameId < 0) throw new InvalidOffsetException(frameId);
            if (data.Length != _frameSize)
                throw new InvalidLengthException(data.Length);

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Full-frame write: no need to snapshot the old frame; just copy once.
            var next = new byte[_frameSize];
            data.CopyTo(next);

            _frames[frameId] = next;
            UpdateMaxFrameWritten(frameId);
            sw.Stop();
            try { StorageMetrics.OnWriteCompleted(_metrics, sw.Elapsed.TotalMilliseconds, _frameSize); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
            return ValueTask.CompletedTask;
        }

        // -------- Offset/length I/O --------

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            token.ThrowIfCancellationRequested();

            if (buffer.Length == 0) return ValueTask.FromResult(0);
            if (offset < 0) throw new InvalidOffsetException(offset);
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (buffer.Length % b) != 0)
                    throw new AlignmentRequiredException(offset, buffer.Length, b);
            }

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            long endExclusive = offset + (long)buffer.Length;
            long deviceLength = GetDeviceLength();
            if (endExclusive > deviceLength)
            {
                sw.Stop();
                if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } }
                throw new DeviceReadOutOfRangeException(offset, buffer.Length, deviceLength);
            }
            long curOffset = offset;
            int totalCopied = 0;
            var dst = buffer.Span;
            while (totalCopied < buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                long frameId = Math.DivRem(curOffset, _frameSize, out long intra);
                int inFrameOffset = (int)intra;
                int canCopy = Math.Min(_frameSize - inFrameOffset, buffer.Length - totalCopied);
                if (!_frames.TryGetValue(frameId, out var arr))
                {
                    try { StorageMetrics.IncrementCacheMiss(_metrics); } catch { }
                    if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } }
                    throw new DeviceReadOutOfRangeException(offset + totalCopied, canCopy, deviceLength);
                }
                try { StorageMetrics.IncrementCacheHit(_metrics); } catch { }
                var src = arr.AsSpan(inFrameOffset, canCopy);
                src.CopyTo(dst.Slice(totalCopied, canCopy));
                totalCopied += canCopy;
                curOffset += canCopy;
            }
            sw.Stop();
            try { StorageMetrics.OnReadCompleted(_metrics, sw.Elapsed.TotalMilliseconds, totalCopied); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
            return ValueTask.FromResult(totalCopied);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            token.ThrowIfCancellationRequested();

            if (data.Length == 0) return ValueTask.CompletedTask;
            if (offset < 0) throw new InvalidOffsetException(offset);
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (data.Length % b) != 0)
                    throw new AlignmentRequiredException(offset, data.Length, b);
            }

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            long curOffset = offset;
            int totalWritten = 0;
            var src = data.Span;

            while (totalWritten < data.Length)
            {
                token.ThrowIfCancellationRequested();

                long frameId = Math.DivRem(curOffset, _frameSize, out long intra);
                int inFrameOffset = (int)intra;
                int canCopy = Math.Min(_frameSize - inFrameOffset, data.Length - totalWritten);

                // Snapshot existing, create next, overlay the slice, then publish atomically
                var existing = GetFrameSnapshot(frameId);
                var next = new byte[_frameSize];
                existing.CopyTo(next, 0);
                src.Slice(totalWritten, canCopy).CopyTo(next.AsSpan(inFrameOffset, canCopy));
                _frames[frameId] = next;
                UpdateMaxFrameWritten(frameId);

                totalWritten += canCopy;
                curOffset += canCopy;
            }

            sw.Stop();
            try { StorageMetrics.OnWriteCompleted(_metrics, sw.Elapsed.TotalMilliseconds, data.Length); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
            return ValueTask.CompletedTask;
        }

        // -------- Helpers --------

        private byte[] GetFrameSnapshot(long frameId)
        {
            return _frames.TryGetValue(frameId, out var arr) ? arr : _zeroFrame;
        }
        private void UpdateMaxFrameWritten(long frameId)
        {
            long candidate = frameId;
            long current;
            do
            {
                current = Interlocked.Read(ref _maxFrameWritten);
                if (candidate <= current) return;
            }
            while (Interlocked.CompareExchange(ref _maxFrameWritten, candidate, current) != current);
        }

        private long GetDeviceLength()
        {
            long max = Interlocked.Read(ref _maxFrameWritten);
            if (max < 0) return 0;
            long blocks = max + 1;
            long size = (long)_frameSize;
            // Safe multiply with saturation to long.MaxValue on overflow risk
            if (blocks > 0 && size > 0)
            {
                long limit = long.MaxValue / size;
                if (blocks > limit) return long.MaxValue;
            }
            return blocks * size;
        }

    }
}
