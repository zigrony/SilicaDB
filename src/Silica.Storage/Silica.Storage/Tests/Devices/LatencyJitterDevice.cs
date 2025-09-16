using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager, TagKeys
using Silica.Storage.Metrics; // StorageMetrics
using Silica.DiagnosticsCore.Tracing; // TraceManager
using Silica.Storage.Exceptions;

namespace Silica.Storage.Tests.Devices
{
    public sealed class LatencyJitterDevice : IStorageDevice, IMountableStorage
    {
        private readonly IStorageDevice _inner;
        private readonly TimeSpan _minDelay, _maxDelay;
        // Use thread-safe shared RNG

        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;
        private readonly object _stateLock = new();
        private bool _mounted;
        private int _disposedFlag; // 0 = not disposed, 1 = disposed


        public LatencyJitterDevice(IStorageDevice inner, TimeSpan minDelay, TimeSpan maxDelay)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            _inner = inner;
            // Normalize and clamp delays to avoid negative or inverted ranges
            if (maxDelay < minDelay)
            {
                var t = minDelay;
                minDelay = maxDelay;
                maxDelay = t;
            }
            if (minDelay < TimeSpan.Zero) minDelay = TimeSpan.Zero;
            if (maxDelay < TimeSpan.Zero) maxDelay = TimeSpan.Zero;
            _minDelay = minDelay;
            _maxDelay = maxDelay;
            _componentName = GetType().Name;
            _metrics = DiagnosticsCoreBootstrap.IsStarted ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager();
            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                try
                {
                    StorageMetrics.RegisterAll(
                        _metrics,
                        deviceComponentName: _componentName,
                        frameLockCacheSizeProvider: null,
                        freeSpaceProvider: null,
                        queueDepthProvider: null,
                        inFlightProvider: null);
                    _metricsRegistered = true;
                }
                catch { /* swallow */ }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureNotDisposed()
        {
            if (Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();
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
                        inFlightProvider: null);
                    _metricsRegistered = true;
                }
                catch { /* swallow */ }
            }
        }

        public StorageGeometry Geometry => _inner.Geometry;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureMounted()
        {
            // Prefer disposed signal for clarity
            if (Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();

            lock (_stateLock)
            {
                if (!_mounted)
                    throw new DeviceNotMountedException();
            }
        }


        private Task DelayAsync(CancellationToken ct)
        {
            EnsureNotDisposed();
            EnsureMounted();
            EnsureMetricsRegistered();
            var baseMs = _minDelay.TotalMilliseconds;
            var spanMs = (_maxDelay - _minDelay).TotalMilliseconds;
            if (spanMs < 0) spanMs = 0; // defensive clamp
            var jitterMs = baseMs + Random.Shared.NextDouble() * spanMs;
            if (jitterMs < 0) jitterMs = 0; // clamp before bucketing/delay
            int bucketMs = jitterMs > int.MaxValue ? int.MaxValue : (int)jitterMs;

            // Emit a simple counter and a trace for the applied jitter (operator-visible, low-cardinality)
            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
            var bucket = GetJitterBucket(bucketMs);
            TryEmitTrace("delay", "info", "latency_jitter_applied", bucket);

            return Task.Delay(TimeSpan.FromMilliseconds(jitterMs), ct);
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            await DelayAsync(ct).ConfigureAwait(false);
            return await _inner.ReadAsync(offset, destination, ct).ConfigureAwait(false);
        }

        public async ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> destination, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            await DelayAsync(ct).ConfigureAwait(false);
            return await _inner.ReadFrameAsync(frameId, destination, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            await DelayAsync(ct).ConfigureAwait(false);
            await _inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            await DelayAsync(ct).ConfigureAwait(false);
            await _inner.WriteFrameAsync(frameId, source, ct).ConfigureAwait(false);
        }

        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            EnsureMetricsRegistered();
            return _inner.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            // Best-effort local lifecycle hygiene for wrapper
            lock (_stateLock) { _mounted = false; }
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
                return;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        // --- IMountableStorage with explicit lifecycle gating ---
        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (_inner is IMountableStorage mountable)
                await mountable.MountAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateLock) { _mounted = true; }
        }

        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock) { _mounted = false; }
            if (_inner is IMountableStorage mountable)
                await mountable.UnmountAsync(cancellationToken).ConfigureAwait(false);
        }

        private void TryEmitTrace(string operation, string status, string message, string fieldValue)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            try
            {
                // Use only allowed tag keys to avoid schema drift
                var tags = new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                {
                    { TagKeys.Field, fieldValue }
                };
                DiagnosticsCoreBootstrap.Instance.Traces.Emit(
                    component: _componentName,
                    operation: operation,
                    status: status,
                    tags: tags,
                    message: message);
            }
            catch { /* swallow trace emission faults */ }
        }

        private static string GetJitterBucket(int jitterMs)
        {
            // 10ms buckets up to 500ms, then "500ms_plus"
            if (jitterMs < 0) jitterMs = 0;
            if (jitterMs > 500) return "jitter_500ms_plus";
            int low = jitterMs / 10 * 10;
            int high = low + 9;
            // Build "jitter_XX_YYms" without allocations beyond a few ints
            // (simple string concatenation is fine here)
            return "jitter_" + low.ToString() + "_" + high.ToString() + "ms";
        }

    }
}