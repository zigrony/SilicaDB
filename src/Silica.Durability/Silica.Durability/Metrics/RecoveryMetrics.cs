namespace Silica.Durability.Metrics
{
    using System;
    using System.Collections.Generic;
    using Silica.DiagnosticsCore.Metrics;

    public static class RecoveryMetrics
    {
        // --------------------------
        // START / STOP
        // --------------------------
        public static readonly MetricDefinition StartCount = new(
            Name: "recovery.start.count",
            Type: MetricType.Counter,
            Description: "Number of recovery start operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition StartDurationMs = new(
            Name: "recovery.start.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of recovery startup",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition StopCount = new(
            Name: "recovery.stop.count",
            Type: MetricType.Counter,
            Description: "Number of recovery stop operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition StopDurationMs = new(
            Name: "recovery.stop.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of recovery shutdown",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // RECORD REPLAY
        // --------------------------
        public static readonly MetricDefinition RecordReadCount = new(
            Name: "recovery.record_read.count",
            Type: MetricType.Counter,
            Description: "Number of WAL records replayed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition RecordReadDurationMs = new(
            Name: "recovery.record_read.duration_ms",
            Type: MetricType.Histogram,
            Description: "Latency of individual record reads",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition RecordReadFailures = new(
            Name: "recovery.record_read.failures",
            Type: MetricType.Counter,
            Description: "Count of failed record read attempts",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CORRUPT / TORN TAIL DETECTION
        // --------------------------
        public static readonly MetricDefinition CorruptTailCount = new(
            Name: "recovery.corrupt_tail.count",
            Type: MetricType.Counter,
            Description: "Number of times a corrupt or torn WAL tail was encountered during recovery",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // PAYLOAD SIZE TRACKING
        // --------------------------
        public static readonly MetricDefinition PayloadBytesRead = new(
            Name: "recovery.payload.bytes",
            Type: MetricType.Histogram,
            Description: "Total bytes read from WAL payloads during recovery",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION
        // =====================================================================
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName = "RecoverManager")
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = "RecoverManager";

            var defTag = new KeyValuePair<string, object>(
                TagKeys.Component,
                componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Start / Stop
            Reg(StartCount);
            Reg(StartDurationMs);
            Reg(StopCount);
            Reg(StopDurationMs);

            // Record replay
            Reg(RecordReadCount);
            Reg(RecordReadDurationMs);
            Reg(RecordReadFailures);

            // Corrupt/torn tail detection
            Reg(CorruptTailCount);

            // Payload size tracking
            Reg(PayloadBytesRead);
        }

        private static void TryRegister(
            IMetricsManager metrics,
            MetricDefinition def)
        {
            try { metrics.Register(def); }
            catch { /* swallow duplicate or non-fatal errors */ }
        }
    }
}
