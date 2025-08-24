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
        private int _size;
        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;
        public BoundedInMemoryTraceSink(int maxSize, IMetricsManager? metrics = null, bool registerGauges = true)
        {
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size must be positive.");

            _maxSize = maxSize;
            _metrics = metrics;
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
            }
        }

        public void Emit(TraceEvent evt)
        {
            if (evt is null) throw new ArgumentNullException(nameof(evt));

            // Reserve a slot atomically
            while (true)
            {
                var cur = Volatile.Read(ref _size);
                if (cur >= _maxSize)
                {
                    Interlocked.Increment(ref _droppedCount);
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.SinkOverflow),
                    new KeyValuePair<string, object>(TagKeys.Sink, nameof(BoundedInMemoryTraceSink)));
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
        }

    public void Dispose() => Clear();
    }
}
