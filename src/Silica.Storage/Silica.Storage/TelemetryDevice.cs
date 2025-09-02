using System;
using System.Threading;
using System.Buffers.Binary;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.DiagnosticsCore.Extensions.Storage; // StorageMetrics

namespace Silica.Storage
{
    public sealed class TelemetryDevice : IStorageDevice, IMountableStorage
    {
        private long _reads;
        private long _writes;
        // DiagnosticsCore
        private readonly IMetricsManager _metrics;
        private readonly string _componentName;
        private readonly StorageMetrics.IInFlightProvider _inFlightGauge;
        private readonly object _stateLock = new();
        private bool _mounted;


        public TelemetryDevice(
            int logicalBlockSize = 4096,
            int maxIoBytes = 4 * 1024 * 1024,
            bool requiresAlignedIo = false,
            bool supportsFua = false)
        {
            if (logicalBlockSize <= 0) throw new ArgumentOutOfRangeException(nameof(logicalBlockSize));
            if (maxIoBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxIoBytes));
            if (maxIoBytes > 0 && (maxIoBytes % logicalBlockSize) != 0)
                throw new ArgumentException("maxIoBytes must be a multiple of logicalBlockSize when > 0.", nameof(maxIoBytes));

            Geometry = new StorageGeometry
            {
                LogicalBlockSize = logicalBlockSize,
                MaxIoBytes = maxIoBytes,
                RequiresAlignedIo = requiresAlignedIo,
                SupportsFua = supportsFua
            };

            _componentName = GetType().Name;
            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                _metrics = DiagnosticsCoreBootstrap.Instance.Metrics;
            }
            else
            {
                _metrics = new NoOpMetricsManager();
            }
            _inFlightGauge = StorageMetrics.CreateInFlightCounter();

            try
            {
                StorageMetrics.RegisterAll(
                    _metrics,
                    deviceComponentName: _componentName,
                    frameLockCacheSizeProvider: null,
                    freeSpaceProvider: null,
                    queueDepthProvider: null,
                    inFlightProvider: _inFlightGauge);
            }
            catch { /* swallow registration/exporter issues */ }
        }

        public StorageGeometry Geometry { get; }

        public (long Reads, long Writes) Snapshot() => (Interlocked.Read(ref _reads), Interlocked.Read(ref _writes));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureMounted()
        {
            lock (_stateLock)
            {
                if (!_mounted) throw new InvalidOperationException("Device is not mounted");
            }
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
        {
            EnsureMounted();
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (buffer.Length % b) != 0)
                    throw new ArgumentException("Aligned I/O required: offset and length must be multiples of LogicalBlockSize.");
            }

            Interlocked.Increment(ref _reads);

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Frame I/O is exact-size by contract; always fill full frame.
            var span = buffer.Span;

            // Pattern: first 8 bytes = little-endian offset; rest zero (portable LE).
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, offset);

            int toCopy = tmp.Length <= span.Length ? tmp.Length : span.Length;
            tmp.Slice(0, toCopy).CopyTo(span);
            if (span.Length > toCopy)
            {
                var rest = span.Slice(toCopy);
                rest.Clear();
            }

            sw.Stop();
            try { StorageMetrics.OnReadCompleted(_metrics, sw.Elapsed.TotalMilliseconds, buffer.Length); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }

            return ValueTask.FromResult(buffer.Length);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            EnsureMounted();
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (data.Length % b) != 0)
                    throw new ArgumentException("Aligned I/O required: offset and length must be multiples of LogicalBlockSize.");
            }

            Interlocked.Increment(ref _writes);
            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Discard write; telemetry-only.
            sw.Stop();
            try { StorageMetrics.OnWriteCompleted(_metrics, sw.Elapsed.TotalMilliseconds, data.Length); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
        {
            EnsureMounted();
            token.ThrowIfCancellationRequested();
            if (frameId < 0) throw new ArgumentOutOfRangeException(nameof(frameId));
            if (buffer.Length != Geometry.LogicalBlockSize)
                throw new ArgumentException($"Frame I/O requires exactly {Geometry.LogicalBlockSize} bytes", nameof(buffer));

            // Use same pattern as offset-based read, but seed with frameId.
            Interlocked.Increment(ref _reads);

            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var span = buffer.Span;

            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, frameId);

            int toCopy = Math.Min(span.Length, tmp.Length);
            tmp.Slice(0, toCopy).CopyTo(span);
            if (span.Length > toCopy)
                span.Slice(toCopy).Clear();

            sw.Stop();
            try { StorageMetrics.OnReadCompleted(_metrics, sw.Elapsed.TotalMilliseconds, buffer.Length); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
            return ValueTask.FromResult(buffer.Length);
        }

        public ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            EnsureMounted();
            token.ThrowIfCancellationRequested();
            if (frameId < 0) throw new ArgumentOutOfRangeException(nameof(frameId));
            if (data.Length != Geometry.LogicalBlockSize)
                throw new ArgumentException($"Frame I/O requires exactly {Geometry.LogicalBlockSize} bytes", nameof(data));

            Interlocked.Increment(ref _writes);
            bool gaugeAdded = false;
            try { _inFlightGauge.Add(1); gaugeAdded = true; } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Discard write; telemetry-only.
            sw.Stop();
            try { StorageMetrics.OnWriteCompleted(_metrics, sw.Elapsed.TotalMilliseconds, data.Length); }
            catch { }
            finally { if (gaugeAdded) { try { _inFlightGauge.Add(-1); } catch { } } }
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken token = default)
        {
            EnsureMounted();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // --- IMountableStorage (explicit mount gating) ---
        public Task MountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock) { _mounted = true; }
            return Task.CompletedTask;
        }
        public Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock) { _mounted = false; }
            return Task.CompletedTask;
        }

    }
}
