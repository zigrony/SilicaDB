// File: Silica.DiagnosticsCore/Tracing/Sinks/BoundedInMemoryTraceSink.cs
using Silica.DiagnosticsCore.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// In-memory, fixed-size buffer of TraceEvent.
    /// - Append-only until Clear() is called; retained count is monotonic within a lifecycle.
    /// - Drops and counts overflow safely with drop_cause="sink_overflow".
    /// - If a rolling window is desired, add a dequeue path and decrement _size accordingly.    /// </summary>
    public sealed class BoundedInMemoryTraceSink : ITraceSink, IDisposable
    {
        private readonly ConcurrentQueue<TraceEvent> _buffer = new();
        private readonly int _maxSize;
        private long _droppedCount;
        private long _evictedCount;
        private int _size;
        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;
        private readonly bool _evictOldestOnOverflow;
        private int _disposed;
        private void TryRegisterDropsMetric()
        {
            if (_metrics is null) return;
            try
            {
                if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TracesDropped);
                else _metrics.Register(DiagCoreMetrics.TracesDropped);
            }
            catch { /* swallow */ }
        }

        public BoundedInMemoryTraceSink(int maxSize, IMetricsManager? metrics = null, bool registerGauges = true, bool evictOldestOnOverflow = false)
        {
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size must be positive.");

            _maxSize = maxSize;
            _metrics = metrics;
            _evictOldestOnOverflow = evictOldestOnOverflow;
            // Observable utilization ratio via canonical metrics manager (single meter in the process)
            if (_metrics is not null && registerGauges)
            {
                // Local helper: if facade, TryRegister to avoid duplicate schema exceptions
                void RegisterOnce(Silica.DiagnosticsCore.Metrics.MetricDefinition def)
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(def);
                    else _metrics.Register(def);
                }
                // Ensure drop counter exists for overflow accounting
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TracesDropped);
                    else _metrics.Register(DiagCoreMetrics.TracesDropped);
                }
                catch { /* swallow */ }

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.bounded_buffer.dropped_total",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Total traces dropped due to sink overflow (monotonic within lifecycle/reset)",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () =>
                    {
                        var dropped = Interlocked.Read(ref _droppedCount);
                        return new[] { new System.Diagnostics.Metrics.Measurement<long>(dropped) };
                    }));

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: DiagCoreMetrics.BoundedBufferEvictedTotal.Name,
                    Type: DiagCoreMetrics.BoundedBufferEvictedTotal.Type,
                    Description: DiagCoreMetrics.BoundedBufferEvictedTotal.Description,
                    Unit: DiagCoreMetrics.BoundedBufferEvictedTotal.Unit,
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () =>
                    {
                        var ev = Interlocked.Read(ref _evictedCount);
                        return new[] { new System.Diagnostics.Metrics.Measurement<long>(ev) };
                    }));

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.bounded_buffer.utilization",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Retained fraction of bounded trace buffer",
                    Unit: "ratio",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    DoubleCallback: () =>
                    {
                        var size = Volatile.Read(ref _size);
                        return new[] { new System.Diagnostics.Metrics.Measurement<double>(size / (double)_maxSize) };
                    }));

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.bounded_buffer.size",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Current buffered trace count",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () =>
                    {
                        var size = Volatile.Read(ref _size);
                        return new[] { new System.Diagnostics.Metrics.Measurement<long>(size) };
                    }));

            }
        }

        public void Emit(TraceEvent evt)
        {
            if (evt is null) throw new ArgumentNullException(nameof(evt));
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
            {
                // Defensive: sink should not receive events after disposal
                try
                {
                    TryRegisterDropsMetric();
                    _metrics?.Increment(
                        DiagCoreMetrics.TracesDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InvalidOperation),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(BoundedInMemoryTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _evictOldestOnOverflow ? "evict_oldest" : "append_only"));
                }
                catch { /* swallow */ }
                return;
            }


            // Reserve a slot atomically
            while (true)
            {
                var cur = Volatile.Read(ref _size);
                if (cur >= _maxSize)
                {
                    if (_evictOldestOnOverflow)
                    {
                        if (_buffer.TryDequeue(out _))
                        {
                            // Replace oldest with newest; account eviction and keep size unchanged
                            Interlocked.Increment(ref _evictedCount);
                            // size stays at max; proceed to enqueue new below without changing _size
                            break;
                        }
                        // If dequeue failed spuriously, fall through to drop
                    }
                    // Drop newest
                    Interlocked.Increment(ref _droppedCount);
                    TryRegisterDropsMetric();
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.SinkOverflow),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(BoundedInMemoryTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _evictOldestOnOverflow ? "evict_oldest" : "drop_newest"));
                    return;
                }
                if (Interlocked.CompareExchange(ref _size, cur + 1, cur) == cur) break;
            }

            _buffer.Enqueue(evt);
            // No decrement here because the buffer represents retained entries.
            // If you add a TryDequeue path, decrement _size accordingly.
        }

        /// <summary>
        /// Snapshot of current buffered events.
        /// </summary>
        public IReadOnlyCollection<TraceEvent> GetSnapshot()
            => _buffer.ToArray();

        /// <summary>
        /// Total number of events dropped due to buffer overflow.
        /// </summary>
        public long DroppedCount
            => Interlocked.Read(ref _droppedCount);

        public void Clear()
        {
            while (_buffer.TryDequeue(out _)) { }
            Volatile.Write(ref _size, 0);
        }


        /// <summary>
        /// Clears the buffer and resets drop counters.
        /// Intended for test harnesses or baselining windows.
        /// </summary>
        public void ResetStats()
        {
            Clear();
            Interlocked.Exchange(ref _droppedCount, 0);
            Interlocked.Exchange(ref _evictedCount, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Clear();
        }
    }
}
