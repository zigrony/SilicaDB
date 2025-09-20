namespace Silica.Durability.Metrics
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Metrics;
    using Silica.DiagnosticsCore.Metrics;

    public static class WalMetrics
    {
        // --------------------------
        // START / STOP COUNTERS
        // --------------------------
        public static readonly MetricDefinition StartCount = new(
            Name: "wal.start.count",
            Type: MetricType.Counter,
            Description: "Number of WAL start operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition StopCount = new(
            Name: "wal.stop.count",
            Type: MetricType.Counter,
            Description: "Number of WAL stop operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // START / STOP DURATIONS
        // --------------------------
        public static readonly MetricDefinition StartDurationMs = new(
            Name: "wal.start.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of WAL startup",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition StopDurationMs = new(
            Name: "wal.stop.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of WAL shutdown",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // APPEND / FLUSH
        // --------------------------
        public static readonly MetricDefinition AppendCount = new(
            Name: "wal.append.count",
            Type: MetricType.Counter,
            Description: "Number of WAL appends",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition AppendDurationMs = new(
            Name: "wal.append.duration_ms",
            Type: MetricType.Histogram,
            Description: "Latency of WAL append operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesAppended = new(
            Name: "wal.append.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes written to WAL",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FlushCount = new(
            Name: "wal.flush.count",
            Type: MetricType.Counter,
            Description: "Number of WAL flush operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FlushDurationMs = new(
            Name: "wal.flush.duration_ms",
            Type: MetricType.Histogram,
            Description: "Latency of WAL flush operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // LOCK / IN_FLIGHT
        // --------------------------
        public static readonly MetricDefinition LockWaitDurationMs = new(
            Name: "wal.lock.wait_time_ms",
            Type: MetricType.Histogram,
            Description: "Time spent waiting for WAL lock",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition InFlightOps = new(
            Name: "wal.inflight.ops",
            Type: MetricType.ObservableGauge,
            Description: "Concurrent WAL operations in flight",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // LATEST LSN GAUGE
        // --------------------------
        public static readonly MetricDefinition LatestLsn = new(
            Name: "wal.latest_sequence_number",
            Type: MetricType.ObservableGauge,
            Description: "Highest WAL sequence number assigned",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TAIL TRUNCATION
        // --------------------------
        public static readonly MetricDefinition TailTruncateCount = new(
            Name: "wal.tail_truncate.count",
            Type: MetricType.Counter,
            Description: "Number of times WAL tail was truncated due to corruption or partial writes",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // DIRECTORY FLUSH UNSUPPORTED
        // --------------------------
        public static readonly MetricDefinition DirectoryFlushUnsupportedCount = new(
            Name: "wal.directory_flush_unsupported.count",
            Type: MetricType.Counter,
            Description: "Number of times a directory flush was attempted but not supported by the platform or filesystem",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CONTRACT / FLUSH VALIDATION
        // --------------------------
        public static readonly MetricDefinition SequenceMismatchCount = new(
            Name: "wal.sequence.mismatch.count",
            Type: MetricType.Counter,
            Description: "Number of WAL appends where caller-supplied SequenceNumber did not match assigned LSN",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FlushRegressionCount = new(
            Name: "wal.flush.regression.count",
            Type: MetricType.Counter,
            Description: "Number of WAL flushes where post-flush file length was smaller than pre-flush",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION
        // =====================================================================
        public static void RegisterAll(
            IMetricsManager metrics,
            string walComponentName,
            Func<long> latestLsnProvider,
            Func<long> inFlightOpsProvider)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(walComponentName))
                walComponentName = "WalManager";

            var defTag = new KeyValuePair<string, object>(
                TagKeys.Component,
                walComponentName);
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

            // Append / Flush
            Reg(AppendCount);
            Reg(AppendDurationMs);
            Reg(BytesAppended);
            Reg(FlushCount);
            Reg(FlushDurationMs);

            // Lock / In-flight
            Reg(LockWaitDurationMs);
            var inflightGauge = InFlightOps with
            {
                LongCallback = () =>
                    new[] { new Measurement<long>(inFlightOpsProvider(), defTag) },
                DefaultTags = single
            };
            TryRegister(metrics, inflightGauge);

            // Latest LSN gauge
            var gauge = LatestLsn with
            {
                LongCallback = () =>
                    new[] { new Measurement<long>(latestLsnProvider(), defTag) },
                DefaultTags = single
            };
            TryRegister(metrics, gauge);

            // Tail truncation counter
            var tailTrunc = TailTruncateCount with { DefaultTags = single };
            TryRegister(metrics, tailTrunc);

            // Directory flush unsupported counter
            var dirFlushUnsup = DirectoryFlushUnsupportedCount with { DefaultTags = single };
            TryRegister(metrics, dirFlushUnsup);

            // Contract / flush validation counters
            var mismatch = SequenceMismatchCount with { DefaultTags = single };
            TryRegister(metrics, mismatch);

            var flushRegress = FlushRegressionCount with { DefaultTags = single };
            TryRegister(metrics, flushRegress);
        }

        private static void TryRegister(
            IMetricsManager metrics,
            MetricDefinition def)
        {
            try { metrics.Register(def); }
            catch { /* swallow duplicate or non-fatal errors */ }
        }

        /// <summary>
        /// Increments the WAL tail truncation counter.
        /// </summary>
        public static void IncrementTailTruncateCount(IMetricsManager metrics)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            try { metrics.Increment(TailTruncateCount.Name); }
            catch { /* swallow duplicate or non-fatal errors */ }
        }
    }
}
