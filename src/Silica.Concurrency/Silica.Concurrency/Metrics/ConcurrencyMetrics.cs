using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Concurrency.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality Concurrency metric definitions and helpers.
    /// Mirrors PageAccessMetrics design: contract-first, safe, and reusable.
    /// </summary>
    public static class ConcurrencyMetrics
    {
        // --------------------------
        // LOCK LIFECYCLE
        // --------------------------
        public static readonly MetricDefinition AcquireCancelledCount = new(
            Name: "concurrency.lock.acquire.cancelled.count",
            Type: MetricType.Counter,
            Description: "Acquisition attempts cancelled by caller",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition AcquireAttemptCount = new(
            Name: "concurrency.lock.acquire.attempt.count",
            Type: MetricType.Counter,
            Description: "Lock acquisition attempts (before queueing/grant)",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition AcquireImmediateCount = new(
            Name: "concurrency.lock.acquire.immediate.count",
            Type: MetricType.Counter,
            Description: "Locks granted without waiting",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition AcquireCount = new(
            Name: "concurrency.lock.acquire.count",
            Type: MetricType.Counter,
            Description: "Lock acquisitions granted",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());


        public static readonly MetricDefinition DeadlockGraphSize = new(
            Name: "concurrency.deadlock.graph.size",
            Type: MetricType.Histogram,
            Description: "Number of transactions present in wait-for graph when deadlock detected",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ReleaseCount = new(
            Name: "concurrency.lock.release.count",
            Type: MetricType.Counter,
            Description: "Locks released",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition AcquireLatencyMs = new(
            Name: "concurrency.lock.acquire.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency to acquire a lock (wait duration)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // WAITS / CONTENTION
        // --------------------------
        public static readonly MetricDefinition WaitQueuedCount = new(
            Name: "concurrency.lock.wait.queued.count",
            Type: MetricType.Counter,
            Description: "Waiters queued due to contention",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WaitTimeoutCount = new(
            Name: "concurrency.lock.wait.timeout.count",
            Type: MetricType.Counter,
            Description: "Waits timing out before acquiring a lock",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition QueueDepth = new(
            Name: "concurrency.lock.queue.depth",
            Type: MetricType.ObservableGauge,
            Description: "Approximate queued waiters (per component)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // DEADLOCKS
        // --------------------------
        public static readonly MetricDefinition DeadlockCount = new(
            Name: "concurrency.deadlock.count",
            Type: MetricType.Counter,
            Description: "Deadlocks detected",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TAG FIELDS (low cardinality)
        // --------------------------
        public static class Fields
        {
            public const string Shared = "shared";
            public const string Exclusive = "exclusive";
            public const string Txn = "txn";
            public const string Resource = "resource";
            public const string Mode = "mode";
        }

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName,
            Func<long>? queueDepthProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "Concurrency";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(AcquireAttemptCount);
            Reg(AcquireImmediateCount);
            Reg(AcquireCount);
            Reg(ReleaseCount);
            Reg(AcquireLatencyMs);
            Reg(WaitQueuedCount);
            Reg(WaitTimeoutCount);
            Reg(DeadlockCount);
            Reg(DeadlockGraphSize);

            if (queueDepthProvider is not null)
            {
                var g = QueueDepth with
                {
                    LongCallback = () =>
                    {
                        var v = queueDepthProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // --------------------------
        // HELPERS
        // --------------------------
        public static void IncrementAcquireAttempt(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, AcquireAttemptCount);
            metrics.Increment(AcquireAttemptCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void IncrementAcquireImmediate(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, AcquireImmediateCount);
            metrics.Increment(AcquireImmediateCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void IncrementAcquire(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, AcquireCount);
            metrics.Increment(AcquireCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void IncrementRelease(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, ReleaseCount);
            metrics.Increment(ReleaseCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void RecordAcquireLatency(IMetricsManager metrics, double waitMs, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, AcquireLatencyMs);
            metrics.Record(AcquireLatencyMs.Name, waitMs, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }
        public static void IncrementAcquireCancelled(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, AcquireCancelledCount);
            metrics.Increment(AcquireCancelledCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void IncrementWaitQueued(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, WaitQueuedCount);
            metrics.Increment(WaitQueuedCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }
        public static void RecordDeadlockGraphSize(IMetricsManager metrics, long size)
        {
            if (metrics is null) return;
            TryRegister(metrics, DeadlockGraphSize);
            metrics.Record(DeadlockGraphSize.Name, size);
        }


        public static void IncrementWaitTimeout(IMetricsManager metrics, string mode)
        {
            if (metrics is null) return;
            TryRegister(metrics, WaitTimeoutCount);
            metrics.Increment(WaitTimeoutCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, mode));
        }

        public static void IncrementDeadlock(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, DeadlockCount);
            metrics.Increment(DeadlockCount.Name, 1);
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
