using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager, TagKeys
using Silica.DiagnosticsCore.Extensions.Storage; // StorageMetrics
using Silica.DiagnosticsCore.Tracing; // TraceManager

namespace Silica.Storage
{
    public sealed class FaultInjectionDevice : IStorageDevice, IMountableStorage
    {
        private readonly IStorageDevice _inner;
        private readonly double _faultRate;
        // Use thread-safe shared RNG

        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;
        private readonly object _stateLock = new();
        private bool _mounted;


        public FaultInjectionDevice(IStorageDevice inner, double faultRate = 0.05)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            _inner = inner;
            // Clamp to [0,1] to avoid undefined behavior from misconfiguration
            double rate = faultRate;
            if (rate < 0.0) rate = 0.0;
            if (rate > 1.0) rate = 1.0;
            _faultRate = rate;
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
                catch { /* swallow registration/exporter issues */ }
            }
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
            lock (_stateLock)
            {
                if (!_mounted)
                    throw new InvalidOperationException("Device is not mounted");
            }
        }


        private void MaybeThrow(string operationLabel)
        {
            if (Random.Shared.NextDouble() < _faultRate)
            {
                // Count injected exception fault
                try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                // Trace for operator visibility (allowed tag keys only)
                TryEmitTrace(operationLabel, "warn", "fault_injected_throw", "fault_throw");
                throw new IOException("Injected fault for testing.");
            }
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            MaybeThrow("read");
            var bytesRead = await _inner.ReadAsync(offset, destination, ct).ConfigureAwait(false);

            // Corrupt data with given probability
            if (Random.Shared.NextDouble() < _faultRate && bytesRead > 0)
            {
                destination.Span.Slice(0, bytesRead).Fill(0xFF);
                // Count injected corruption fault
                try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                TryEmitTrace("read", "warn", "fault_injected_corrupt", "fault_corrupt");

            }

            return bytesRead;
        }

        public async ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> destination, CancellationToken ct = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            MaybeThrow("read_frame");
            var bytesRead = await _inner.ReadFrameAsync(frameId, destination, ct).ConfigureAwait(false);

            if (Random.Shared.NextDouble() < _faultRate && bytesRead > 0)
            {
                destination.Span.Slice(0, bytesRead).Fill(0xFF);
                try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                TryEmitTrace("read_frame", "warn", "fault_injected_corrupt", "fault_corrupt");
            }

            return bytesRead;
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            MaybeThrow("write");
            await _inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            MaybeThrow("write_frame");
            await _inner.WriteFrameAsync(frameId, source, ct).ConfigureAwait(false);
        }

        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            EnsureMounted();
            EnsureMetricsRegistered();
            return _inner.FlushAsync(ct);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        // --- IMountableStorage with explicit lifecycle gating ---
        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
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
                // Use only allowed tag keys (Field) to avoid schema drift
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

    }
}