namespace Silica.Durability.Metrics
{
    using System;
    using System.Collections.Generic;
    using Silica.DiagnosticsCore.Metrics;

    public static class CheckpointMetrics
    {
        // --------------------------
        // WRITE
        // --------------------------
        public static readonly MetricDefinition WriteCount = new(
            Name: "checkpoint.write.count",
            Type: MetricType.Counter,
            Description: "Number of checkpoints written",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteDurationMs = new(
            Name: "checkpoint.write.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of checkpoint write operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteFailures = new(
            Name: "checkpoint.write.failures",
            Type: MetricType.Counter,
            Description: "Number of failed checkpoint write attempts",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SkippedWriteCount = new(
            Name: "checkpoint.write.skipped.count",
            Type: MetricType.Counter,
            Description: "Number of checkpoint writes skipped due to no new LSN",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CheckpointFileBytes = new(
            Name: "checkpoint.write.file_bytes",
            Type: MetricType.Histogram,
            Description: "Size of successfully written checkpoint files",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // LOCK
        // --------------------------
        public static readonly MetricDefinition LockWaitDurationMs = new(
            Name: "checkpoint.lock_wait.duration_ms",
            Type: MetricType.Histogram,
            Description: "Time spent waiting to acquire the checkpoint write/prune lock",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // PRUNE
        // --------------------------
        public static readonly MetricDefinition PruneCount = new(
            Name: "checkpoint.prune.count",
            Type: MetricType.Counter,
            Description: "Number of old checkpoints pruned",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition PruneDurationMs = new(
            Name: "checkpoint.prune.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of checkpoint prune operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // READ
        // --------------------------
        public static readonly MetricDefinition ReadCount = new(
            Name: "checkpoint.read.count",
            Type: MetricType.Counter,
            Description: "Number of checkpoint reads",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ReadDurationMs = new(
            Name: "checkpoint.read.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of checkpoint read operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ReadFailures = new(
            Name: "checkpoint.read.failures",
            Type: MetricType.Counter,
            Description: "Number of failed checkpoint read attempts",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // DIRECTORY FLUSH UNSUPPORTED
        // --------------------------
        public static readonly MetricDefinition DirectoryFlushUnsupportedCount = new(
            Name: "checkpoint.directory_flush_unsupported.count",
            Type: MetricType.Counter,
            Description: "Number of times a checkpoint directory flush was attempted but not supported",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION
        // =====================================================================
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName = "CheckpointManager")
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = "CheckpointManager";

            var defTag = new KeyValuePair<string, object>(
                TagKeys.Component,
                componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Write
            Reg(WriteCount);
            Reg(WriteDurationMs);
            Reg(WriteFailures);
            Reg(SkippedWriteCount);
            Reg(CheckpointFileBytes);


            // Lock
            Reg(LockWaitDurationMs);

            // Prune
            Reg(PruneCount);
            Reg(PruneDurationMs);

            // Read
            Reg(ReadCount);
            Reg(ReadDurationMs);
            Reg(ReadFailures);

            // Directory flush unsupported (write/prune paths)
            Reg(DirectoryFlushUnsupportedCount);
        }

        private static void TryRegister(
            IMetricsManager m,
            MetricDefinition def)
        {
            try { m.Register(def); }
            catch { /* swallow duplicate or export errors */ }
        }
    }
}
