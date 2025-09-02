// File: Silica.DiagnosticsCore/Extensions/Storage/StorageMetrics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Extensions.Storage
{
    /// <summary>
    /// Canonical, low-cardinality Storage metric definitions and helpers for DiagnosticsCore.
    /// Temporary, hardcoded extension under Extensions.Storage. Move to Silica.Storage when provider model lands.
    /// </summary>
    /// <remarks>
    /// Design goals:
    /// - Contract-first: stable names, types, units, and allowed tags.
    /// - Low cardinality: use TagKeys.Component for device type (e.g., class name). Avoid instance identifiers.
    /// - No LINQ/reflection/JSON/third-party libs.
    /// - Safe in strict metrics mode; tolerant of multiple registrations across restarts.
    /// </remarks>
    public static class StorageMetrics
    {
        // --------------------------
        // CORE LIFECYCLE METRICS
        // --------------------------

        public static readonly MetricDefinition MountCount = new(
            Name: "storage.mount.count",
            Type: MetricType.Counter,
            Description: "Successful device mounts",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition MountDurationMs = new(
            Name: "storage.mount.duration_ms",
            Type: MetricType.Histogram,
            Description: "Mount duration",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition UnmountCount = new(
            Name: "storage.unmount.count",
            Type: MetricType.Counter,
            Description: "Successful device unmounts",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition UnmountDurationMs = new(
            Name: "storage.unmount.duration_ms",
            Type: MetricType.Histogram,
            Description: "Unmount duration",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CONCURRENCY / LOCKS
        // --------------------------

        public static readonly MetricDefinition LockWaitTimeMs = new(
            Name: "storage.lock.wait_time_ms",
            Type: MetricType.Histogram,
            Description: "Time spent waiting for per-frame locks",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ContentionCount = new(
            Name: "storage.lock.contention.count",
            Type: MetricType.Counter,
            Description: "Lock contention events",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition DeadlockAvoidanceCount = new(
            Name: "storage.lock.deadlock_avoidance.count",
            Type: MetricType.Counter,
            Description: "Deadlock avoidance triggers (e.g., eviction to break waits)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // IO COUNTS / LATENCY / BYTES
        // --------------------------

        public static readonly MetricDefinition ReadCount = new(
            Name: "storage.read.count",
            Type: MetricType.Counter,
            Description: "Read operations completed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteCount = new(
            Name: "storage.write.count",
            Type: MetricType.Counter,
            Description: "Write operations completed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ReadLatencyMs = new(
            Name: "storage.read.latency_ms",
            Type: MetricType.Histogram,
            Description: "Read latency distribution",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteLatencyMs = new(
            Name: "storage.write.latency_ms",
            Type: MetricType.Histogram,
            Description: "Write latency distribution",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesRead = new(
            Name: "storage.read.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes read",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesWritten = new(
            Name: "storage.write.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes written",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition RetryCount = new(
            Name: "storage.io.retry.count",
            Type: MetricType.Counter,
            Description: "Retries for failed I/O operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CAPACITY & CACHE
        // --------------------------

        public static readonly MetricDefinition FreeSpaceBytes = new(
            Name: "storage.capacity.free_bytes",
            Type: MetricType.ObservableGauge,
            Description: "Free space available on device",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CacheHitCount = new(
            Name: "storage.cache.hit.count",
            Type: MetricType.Counter,
            Description: "Cache hits",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CacheMissCount = new(
            Name: "storage.cache.miss.count",
            Type: MetricType.Counter,
            Description: "Cache misses",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FrameLockCacheSize = new(
            Name: "storage.framelock.cache_size",
            Type: MetricType.ObservableGauge,
            Description: "Current number of cached frame locks",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // PIPELINE / QUEUES
        // --------------------------

        public static readonly MetricDefinition QueueDepth = new(
            Name: "storage.queue.depth",
            Type: MetricType.ObservableGauge,
            Description: "Pending operations in internal queues",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FlushLatencyMs = new(
            Name: "storage.flush.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of flush operations to persistence",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CompactionCount = new(
            Name: "storage.compaction.count",
            Type: MetricType.Counter,
            Description: "Number of compaction events",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CompactionDurationMs = new(
            Name: "storage.compaction.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of compaction operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // EVICTIONS
        // --------------------------

        public static readonly MetricDefinition EvictionCount = new(
            Name: "storage.eviction.count",
            Type: MetricType.Counter,
            Description: "Eviction events (e.g., idle frame locks removed)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // IN-FLIGHT OPERATIONS
        // --------------------------

        public static readonly MetricDefinition InFlightOps = new(
            Name: "storage.inflight.ops",
            Type: MetricType.ObservableGauge,
            Description: "In-flight storage operations (best-effort, per device)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION AND HELPERS
        // =====================================================================

        public static void RegisterAll(
            IMetricsManager metrics,
            string deviceComponentName,
            Func<int>? frameLockCacheSizeProvider = null,
            Func<long>? freeSpaceProvider = null,
            Func<int>? queueDepthProvider = null,
            IInFlightProvider? inFlightProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(deviceComponentName))
                deviceComponentName = "StorageDevice";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, deviceComponentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Core
            Reg(MountCount);
            Reg(MountDurationMs);
            Reg(UnmountCount);
            Reg(UnmountDurationMs);

            // Locks
            Reg(LockWaitTimeMs);
            Reg(ContentionCount);
            Reg(DeadlockAvoidanceCount);

            // IO
            Reg(ReadCount);
            Reg(WriteCount);
            Reg(ReadLatencyMs);
            Reg(WriteLatencyMs);
            Reg(BytesRead);
            Reg(BytesWritten);
            Reg(RetryCount);

            // Capacity & cache
            Reg(CacheHitCount);
            Reg(CacheMissCount);
            Reg(EvictionCount);

            if (frameLockCacheSizeProvider is not null)
            {
                var gauge = FrameLockCacheSize with
                {
                    LongCallback = () =>
                    {
                        var val = frameLockCacheSizeProvider();
                        return new[] { new Measurement<long>(val, defTag) };
                    }
                };
                TryRegister(metrics, gauge);
            }

            if (freeSpaceProvider is not null)
            {
                var gauge = FreeSpaceBytes with
                {
                    LongCallback = () =>
                    {
                        var val = freeSpaceProvider();
                        return new[] { new Measurement<long>(val, defTag) };
                    }
                };
                TryRegister(metrics, gauge);
            }

            // Pipeline / queues
            if (queueDepthProvider is not null)
            {
                var gauge = QueueDepth with
                {
                    LongCallback = () =>
                    {
                        var val = queueDepthProvider();
                        return new[] { new Measurement<long>(val, defTag) };
                    }
                };
                TryRegister(metrics, gauge);
            }
            Reg(FlushLatencyMs);
            Reg(CompactionCount);
            Reg(CompactionDurationMs);

            // In-flight ops gauge
            if (inFlightProvider is not null)
            {
                var gauge = InFlightOps with
                {
                    LongCallback = () =>
                    {
                        var val = inFlightProvider.Current;
                        return new[] { new Measurement<long>(val, defTag) };
                    }
                };
                TryRegister(metrics, gauge);
            }
        }

        // -----------------------------------------------------------------
        // EVENT EMISSION HELPERS
        // -----------------------------------------------------------------

        public static void OnMountCompleted(IMetricsManager metrics, double durationMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, MountCount);
            TryRegister(metrics, MountDurationMs);
            try
            {
                metrics.Increment(MountCount.Name, 1);
                metrics.Record(MountDurationMs.Name, durationMs);
            }
            catch { }
        }

        public static void OnUnmountCompleted(IMetricsManager metrics, double durationMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, UnmountCount);
            TryRegister(metrics, UnmountDurationMs);
            try
            {
                metrics.Increment(UnmountCount.Name, 1);
                metrics.Record(UnmountDurationMs.Name, durationMs);
            }
            catch { }
        }

        public static void RecordLockWaitMs(IMetricsManager metrics, double waitMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, LockWaitTimeMs);
            try { metrics.Record(LockWaitTimeMs.Name, waitMs); } catch { }
        }

        public static void IncrementContention(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, ContentionCount);
            try { metrics.Increment(ContentionCount.Name, 1); } catch { }
        }

        public static void IncrementDeadlockAvoidance(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, DeadlockAvoidanceCount);
            try { metrics.Increment(DeadlockAvoidanceCount.Name, 1); } catch { }
        }

        public static void OnReadCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, ReadCount);
            TryRegister(metrics, ReadLatencyMs);
            TryRegister(metrics, BytesRead);
            try
            {
                metrics.Increment(ReadCount.Name, 1);
                metrics.Record(ReadLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesRead.Name, bytes);
            }
            catch { }
        }

        public static void OnWriteCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, WriteCount);
            TryRegister(metrics, WriteLatencyMs);
            TryRegister(metrics, BytesWritten);
            try
            {
                metrics.Increment(WriteCount.Name, 1);
                metrics.Record(WriteLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesWritten.Name, bytes);
            }
            catch { }
        }

        public static void IncrementRetry(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, RetryCount);
            try { metrics.Increment(RetryCount.Name, 1); } catch { }
        }

        public static void IncrementCacheHit(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, CacheHitCount);
            try { metrics.Increment(CacheHitCount.Name, 1); } catch { }
        }

        public static void IncrementCacheMiss(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, CacheMissCount);
            try { metrics.Increment(CacheMissCount.Name, 1); } catch { }
        }

        public static void IncrementEviction(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, EvictionCount);
            try { metrics.Increment(EvictionCount.Name, 1); } catch { }
        }

        public static void RecordFlushLatency(IMetricsManager metrics, double latencyMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, FlushLatencyMs);
            try { metrics.Record(FlushLatencyMs.Name, latencyMs); } catch { }
        }

        public static void OnCompactionCompleted(IMetricsManager metrics, double durationMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, CompactionCount);
            TryRegister(metrics, CompactionDurationMs);
            try
            {
                metrics.Increment(CompactionCount.Name, 1);
                metrics.Record(CompactionDurationMs.Name, durationMs);
            }
            catch { }
        }

        // -----------------------------------------------------------------
        // INTERNAL
        // -----------------------------------------------------------------

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            if (metrics is MetricsFacade mf) { try { mf.TryRegister(def); } catch { } }
            else { try { metrics.Register(def); } catch { } }
        }

        public interface IInFlightProvider
        {
            long Current { get; }
            void Add(long delta);
        }

        public static IInFlightProvider CreateInFlightCounter()
        {
            return new InFlightCounter();
        }

        private sealed class InFlightCounter : IInFlightProvider
        {
            private long _value;
            public long Current => Interlocked.Read(ref _value);
            public void Add(long delta)
            {
                if (delta == 0) return;
                Interlocked.Add(ref _value, delta);
            }
        }
    }
}
