// File: Silica.DiagnosticsCore/Metrics/DiagCoreMetrics.cs
using System;
using System.Collections.Generic;


namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Canonical metric definitions for Silica.DiagnosticsCore.
    /// These definitions are singletons - they MUST be reused across all code paths
    /// to avoid cardinality drift and to keep aggregation consistent.
    /// </summary>
    public static class DiagCoreMetrics
    {
        // Define NoTags first so all subsequent definitions can safely reference it.
        private static readonly KeyValuePair<string, object>[] NoTags =
            Array.Empty<KeyValuePair<string, object>>();

        public static readonly MetricDefinition MetricTagsTruncated = new(
                                        Name: "diagcore.metrics.tags_truncated",
                                        Type: MetricType.Counter,
                                        Description: "Count of metric tag values truncated to policy",
                                        Unit: "entries",
                                        DefaultTags: NoTags);

        public static readonly MetricDefinition MetricTagsRejected = new(
                                        Name: "diagcore.metrics.tags_rejected",
                                        Type: MetricType.Counter,
                                        Description: "Count of metric tags rejected (disallowed, too many, invalid type)",
                                        Unit: "entries",
                                        DefaultTags: NoTags);

        /// <summary>
        /// Count of individual trace tags dropped by policy (disallowed, too many, invalid type, or value too long).
        /// </summary>
        public static readonly MetricDefinition TraceTagsDropped = new(
            Name: "diagcore.traces.tags_dropped",
            Type: MetricType.Counter,
            Description: "Count of trace tags dropped by policy",
            Unit: "entries",
            DefaultTags: NoTags);

        /// <summary>
        /// Count of individual trace tags dropped by policy (disallowed, too many, invalid, or truncated).
        /// </summary>
        // Count of individual trace tag values truncated to policy
        public static readonly MetricDefinition TraceTagsTruncated = new(
            Name: "diagcore.traces.tags_truncated",
            Type: MetricType.Counter,
            Description: "Count of trace tag values truncated to policy",
            Unit: "entries",
            DefaultTags: NoTags);

        /// <summary>
        /// Count of metric emissions dropped by policy.
        /// </summary>
        public static readonly MetricDefinition MetricsDropped = new(
            Name: "diagcore.metrics.dropped",
            Type: MetricType.Counter,
            Description: "Count of metric emissions dropped by policy",
            Unit: "entries",
            DefaultTags: NoTags
        );

        /// <summary>
        /// Count of trace events dropped by sinks or policy.
        /// </summary>
        public static readonly MetricDefinition TracesDropped = new(
            Name: "diagcore.traces.dropped",
            Type: MetricType.Counter,
            Description: "Count of trace events dropped by sinks or policy",
            Unit: "entries",
            DefaultTags: NoTags
        );

        public static readonly MetricDefinition TracesEmitted = new(
            Name: "diagcore.traces.emitted",
            Type: MetricType.Counter,
            Description: "Count of trace events successfully dispatched",
            Unit: "entries",
            DefaultTags: NoTags
        );

        /// <summary>
        /// Count of trace events that had redaction applied.
        /// </summary>
        public static readonly MetricDefinition TracesRedacted = new(
            Name: "diagcore.traces.redacted",
            Type: MetricType.Counter,
            Description: "Count of trace events that had redaction applied",
            Unit: "entries",
            DefaultTags: NoTags
        );


        /// <summary>
        /// Count of attempts to re-start diagnostics with different options than the active instance.
        /// </summary>
        public static readonly MetricDefinition BootstrapOptionsConflict = new(
            Name: "diagcore.bootstrap.options_conflict",
            Type: MetricType.Counter,
            Description: "Detected divergent DiagnosticsOptions on subsequent Start()",
            Unit: "entries",
            DefaultTags: NoTags
        );

        public static readonly MetricDefinition IgnoredConfigFieldSet = new(
            Name: "diagcore.bootstrap.ignored_config_field_set",
            Type: MetricType.Counter,
            Description: "Count of ignored config fields that were set but not enforced",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition MetricsLenientNoAutoreg = new(
            Name: "diagcore.metrics.lenient_no_autoreg",
            Type: MetricType.Counter,
            Description: "Emissions dropped because strict=false with a manager that does not auto-register",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Dispatcher histogram for sink.Emit duration (ms)
        public static readonly MetricDefinition SinkEmitDurationMs = new(
            Name: "diagcore.traces.sink_emit_duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of sink.Emit calls in milliseconds",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Dispatcher counter for slow sink emits (>1s)
        public static readonly MetricDefinition SinkEmitLong = new(
            Name: "diagcore.traces.sink_emit_long",
            Type: MetricType.Counter,
            Description: "Count of sink.Emit calls exceeding 1s",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Dispatcher histogram for shutdown queue depth on timeout
        public static readonly MetricDefinition DispatcherShutdownQueueDepth = new(
            Name: "diagcore.traces.dispatcher.shutdown_queue_depth",
            Type: MetricType.Histogram,
            Description: "Queue depth measured when dispatcher exceeded shutdown timeout",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ShutdownDisposeErrors = new(
            Name: "diagcore.bootstrap.dispose_errors",
            Type: MetricType.Counter,
            Description: "Count of dispose exceptions encountered during DiagnosticsCore shutdown",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition MetricsLenientAutoregUsed = new(
            Name: "diagcore.metrics.lenient_autoreg_used",
            Type: MetricType.Counter,
            Description: "Emissions proceeded on unregistered metric due to lenient mode with auto-registration inner",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // ---- ConsoleTraceSink self-metrics (observable gauges) ----
        public static readonly MetricDefinition ConsoleSinkQueueDepth = new(
            Name: "diagcore.traces.console_sink.queue_depth",
            Type: MetricType.ObservableGauge,
            Description: "Current queued messages waiting to be written by ConsoleTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkDroppedTotal = new(
            Name: "diagcore.traces.console_sink.dropped_total",
            Type: MetricType.ObservableGauge,
            Description: "Total messages dropped inside ConsoleTraceSink (channel full)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkMessagesWrittenTotal = new(
            Name: "diagcore.traces.console_sink.messages_written_total",
            Type: MetricType.ObservableGauge,
            Description: "Total messages written by ConsoleTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkWriteErrorsTotal = new(
            Name: "diagcore.traces.console_sink.write_errors_total",
            Type: MetricType.ObservableGauge,
            Description: "Total write errors encountered by ConsoleTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkActive = new(
            Name: "diagcore.traces.console_sink.active",
            Type: MetricType.ObservableGauge,
            Description: "Whether ConsoleTraceSink worker is active (1) or stopped (0)",
            Unit: "flag",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkLastWriteLatencyMs = new(
            Name: "diagcore.traces.console_sink.last_write_latency_ms",
            Type: MetricType.ObservableGauge,
            Description: "Latency of the last write observed by ConsoleTraceSink (ms, DEBUG only)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

 
        // ---- ConsoleTraceSink production counters and histogram ----
        public static readonly MetricDefinition ConsoleSinkMessagesWritten = new(
            Name: "diagcore.traces.console_sink.messages_written",
            Type: MetricType.Counter,
            Description: "Messages written by ConsoleTraceSink",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkWriteErrors = new(
            Name: "diagcore.traces.console_sink.write_errors",
            Type: MetricType.Counter,
            Description: "Write errors encountered by ConsoleTraceSink",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkDrops = new(
            Name: "diagcore.traces.console_sink.drops",
            Type: MetricType.Counter,
            Description: "Messages dropped inside ConsoleTraceSink (channel or policy)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkWriteLatencyMs = new(
            Name: "diagcore.traces.console_sink.write_latency_ms",
            Type: MetricType.Histogram,
            Description: "Console write latency distribution (ms)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // ConsoleTraceSink: count of writes exceeding the guard threshold (not a drop)
        public static readonly MetricDefinition ConsoleSinkWriteLatencyExceeded = new(
            Name: "diagcore.traces.console_sink.write_latency_exceeded",
            Type: MetricType.Counter,
            Description: "Count of writes that exceeded the configured latency guard in ConsoleTraceSink",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkWaits = new(
            Name: "diagcore.traces.console_sink.waits",
            Type: MetricType.Counter,
            Description: "Bounded waits before enqueue due to BlockWithTimeout policy",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ConsoleSinkWaitTimeouts = new(
            Name: "diagcore.traces.console_sink.wait_timeouts",
            Type: MetricType.Counter,
            Description: "Timeouts when attempting to enqueue due to BlockWithTimeout policy",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition PumpsRemainingOnTimeout = new(
            Name: "diagcore.traces.dispatcher.pumps_remaining_on_timeout",
            Type: MetricType.Histogram,
            Description: "Count of pump tasks still active when shutdown timed out",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());
        public static readonly MetricDefinition DispatcherSinkRegistered = new(
            Name: "diagcore.traces.dispatcher.sink_registered",
            Type: MetricType.Counter,
            Description: "Count of sinks registered on the dispatcher",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition DispatcherSinkUnregistered = new(
            Name: "diagcore.traces.dispatcher.sink_unregistered",
            Type: MetricType.Counter,
            Description: "Count of sinks unregistered from the dispatcher",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // ---- BoundedInMemoryTraceSink optional rolling-window accounting ----
        public static readonly MetricDefinition BoundedBufferEvictedTotal = new(
            Name: "diagcore.traces.bounded_buffer.evicted_total",
            Type: MetricType.ObservableGauge,
            Description: "Total traces evicted due to evict-oldest overflow policy",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // ---- FileTraceSink metrics ----
        public static readonly MetricDefinition FileSinkWaits = new(
            Name: "diagcore.traces.file_sink.waits",
            Type: MetricType.Counter,
            Description: "Number of times FileTraceSink waited for queue space",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkWaitTimeouts = new(
            Name: "diagcore.traces.file_sink.wait_timeouts",
            Type: MetricType.Counter,
            Description: "Number of times FileTraceSink wait timed out",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkMessagesWritten = new(
            Name: "diagcore.traces.file_sink.messages_written",
            Type: MetricType.Counter,
            Description: "Messages successfully written by FileTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkWriteLatencyMs = new(
            Name: "diagcore.traces.file_sink.write_latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of file writes in milliseconds",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // FileTraceSink: count of writes exceeding the guard threshold (not a drop)
        public static readonly MetricDefinition FileSinkWriteLatencyExceeded = new(
            Name: "diagcore.traces.file_sink.write_latency_exceeded",
            Type: MetricType.Counter,
            Description: "Count of writes that exceeded the configured latency guard in FileTraceSink",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkWriteErrors = new(
            Name: "diagcore.traces.file_sink.write_errors",
            Type: MetricType.Counter,
            Description: "Number of file write errors",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkDrops = new(
            Name: "diagcore.traces.file_sink.drops",
            Type: MetricType.Counter,
            Description: "Number of messages dropped by FileTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkQueueDepth = new(
            Name: "diagcore.traces.file_sink.queue_depth",
            Type: MetricType.ObservableGauge,
            Description: "Current queue depth for FileTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkDroppedTotal = new(
            Name: "diagcore.traces.file_sink.dropped_total",
            Type: MetricType.ObservableGauge,
            Description: "Total dropped messages by FileTraceSink (monotonic within lifecycle/reset)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());


        public static readonly MetricDefinition FileSinkMessagesWrittenTotal = new(
            Name: "diagcore.traces.file_sink.messages_written_total",
            Type: MetricType.ObservableGauge,
            Description: "Total messages written by FileTraceSink",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkWriteErrorsTotal = new(
            Name: "diagcore.traces.file_sink.write_errors_total",
            Type: MetricType.ObservableGauge,
            Description: "Total file write errors",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkActive = new(
            Name: "diagcore.traces.file_sink.active",
            Type: MetricType.ObservableGauge,
            Description: "Whether FileTraceSink is active (1) or inactive (0)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FileSinkLastWriteLatencyMs = new(
            Name: "diagcore.traces.file_sink.last_write_latency_ms",
            Type: MetricType.ObservableGauge,
            Description: "Last observed file write latency in ms",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

    }
}
