// File: MetricsRecorder.cs
// Project: SilicaDB.Metrics
// Implements IMetricsRecorder with safe, idempotent disposal.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Metrics.Interfaces;

namespace SilicaDB.Metrics
{
    public sealed class MetricsRecorder : IMetricsRecorder, IAsyncDisposable
    {
        // Underlying Meter
        private readonly Meter _meter;

        // Queue of pending metric entries
        private readonly ConcurrentQueue<MetricEntry> _queue = new();

        // Signal for the worker
        private readonly SemaphoreSlim _signal = new(0);

        // Cancellation for background loop
        private readonly CancellationTokenSource _cts = new();

        // Background worker task
        private readonly Task _worker;

        // Pending count / flush coordination
        private long _pendingCount;
        private TaskCompletionSource<object>? _flushTcs;
        private readonly object _flushLock = new();

        // Instrument caches
        private readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
        private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
        private readonly ConcurrentDictionary<string, ObservableGauge<double>> _gauges = new();
        private readonly ConcurrentDictionary<string, double> _gaugeValues = new();

        // Disposal guard
        private bool _disposed;

        public MetricsRecorder(string meterName = "SilicaDB.Metrics", string version = "1.0")
        {
            _meter = new Meter(meterName, version);
            _worker = Task.Run(ProcessLoopAsync, CancellationToken.None);
        }

        public ValueTask IncrementCounterAsync(string name, long delta = 1)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

            Enqueue(MetricType.Counter, name, delta);
            return default;
        }

        public ValueTask RecordHistogramAsync(string name, double value)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

            Enqueue(MetricType.Histogram, name, value);
            return default;
        }

        public ValueTask RecordGaugeAsync(string name, double value)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

            Enqueue(MetricType.Gauge, name, value);
            return default;
        }

        public ValueTask FlushAsync()
        {
            // Fast-path: nothing pending
            if (Interlocked.Read(ref _pendingCount) == 0)
                return default;

            lock (_flushLock)
            {
                if (_flushTcs == null || _flushTcs.Task.IsCompleted)
                    _flushTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                return new ValueTask(_flushTcs.Task);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Idempotent
            if (_disposed)
                return;
            _disposed = true;

            // 1) Flush any enqueued work
            await FlushAsync().ConfigureAwait(false);

            // 2) Cancel the background pump
            _cts.Cancel();
            _signal.Release(); // wake it if waiting

            // 3) Wait for it to complete
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }

            // 4) Tear down all resources
            _meter.Dispose();
            _signal.Dispose();
            _cts.Dispose();

            _counters.Clear();
            _histograms.Clear();
            _gauges.Clear();
            _gaugeValues.Clear();
        }

        void IDisposable.Dispose() =>
            DisposeAsync().AsTask().GetAwaiter().GetResult();

        // ---------- Internal helpers ----------

        private void Enqueue(MetricType type, string name, double value)
        {
            if (Interlocked.Increment(ref _pendingCount) == 1)
            {
                lock (_flushLock)
                {
                    _flushTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            _queue.Enqueue(new MetricEntry(type, name, value));
            _signal.Release();
        }

        private async Task ProcessLoopAsync()
        {
            try
            {
                while (true)
                {
                    // Wait for work or cancellation
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);

                    // Drain the queue
                    while (_queue.TryDequeue(out var entry))
                    {
                        try
                        {
                            switch (entry.Type)
                            {
                                case MetricType.Counter:
                                    var ctr = _counters.GetOrAdd(
                                        entry.Name,
                                        n => _meter.CreateCounter<long>(n));
                                    ctr.Add((long)entry.Value);
                                    break;

                                case MetricType.Histogram:
                                    var hist = _histograms.GetOrAdd(
                                        entry.Name,
                                        n => _meter.CreateHistogram<double>(n));
                                    hist.Record(entry.Value);
                                    break;

                                case MetricType.Gauge:
                                    _gaugeValues[entry.Name] = entry.Value;
                                    _gauges.GetOrAdd(
                                        entry.Name,
                                        n => _meter.CreateObservableGauge<double>(
                                            n,
                                            () => new[]
                                            {
                                                new Measurement<double>(_gaugeValues.GetValueOrDefault(n))
                                            }));
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MetricsRecorder] Failed '{entry.Name}': {ex}");
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref _pendingCount) == 0)
                            {
                                lock (_flushLock)
                                {
                                    _flushTcs?.TrySetResult(null);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful exit
            }
        }

        private readonly struct MetricEntry
        {
            public MetricType Type { get; }
            public string Name { get; }
            public double Value { get; }

            public MetricEntry(MetricType type, string name, double value)
            {
                Type = type;
                Name = name;
                Value = value;
            }
        }

        private enum MetricType
        {
            Counter,
            Histogram,
            Gauge
        }
    }
}
