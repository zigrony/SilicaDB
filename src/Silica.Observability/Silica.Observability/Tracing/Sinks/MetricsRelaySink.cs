using Silica.Observability.Metrics;
using Silica.Observability.Metrics.Interfaces;
using Silica.Observability.Tracing;

namespace Silica.Observability.Tracing.Sinks
{

    public sealed class MetricsRelaySink : ITraceSink
    {
        private readonly IMetricsManager _metrics;

        public MetricsRelaySink(IMetricsManager metrics) => _metrics = metrics;

        public void Append(in TraceEvent evt)
        {
            if (evt.Category == TraceCategory.Locks && evt.Type == TraceEventType.Blocked && evt.DurationMicros is int d && d > 10_000)
                _metrics.Increment("locks.wait.long");

            if (evt.Category == TraceCategory.Logger && evt.Type == TraceEventType.Completed && evt.DurationMicros is int f && f > 5_000)
                _metrics.Record("logs.flush.latency.micros", f);

            if (evt.Type == TraceEventType.Error)
                _metrics.Increment("trace.errors");
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}