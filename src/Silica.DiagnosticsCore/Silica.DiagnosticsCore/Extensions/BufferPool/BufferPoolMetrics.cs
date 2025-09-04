// File: Silica.DiagnosticsCore/Extensions/BufferPool/BufferPoolMetrics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Extensions.BufferPool
{
    /// <summary>
    /// Canonical, low-cardinality BufferPool metric definitions and helpers for DiagnosticsCore.
    /// Applies to any in-memory page buffer with pin/unpin, eviction, and writeback semantics.
    /// </summary>
    /// <remarks>
    /// Design goals:
    /// - Contract-first: stable names, types, units, and allowed tags.
    /// - Low cardinality: use TagKeys.Component as the single default tag.
    /// - Optional reason fields go into TagKeys.Field with a small, fixed set.
    /// - No LINQ/reflection/JSON/third-party libraries. Only BCL.
    /// - Safe in strict metrics mode; tolerant of multiple registrations across restarts.
    /// </remarks>
    public static class BufferPoolMetrics
    {
        // --------------------------
        // HIT / MISS / FAULTS
        // --------------------------
        public static readonly MetricDefinition Hits = new(
            Name: "bufferpool.hit.count",
            Type: MetricType.Counter,
            Description: "Cache hits (resident page served without I/O)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition Misses = new(
            Name: "bufferpool.miss.count",
            Type: MetricType.Counter,
            Description: "Cache misses (page needed loading)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition PageFaults = new(
            Name: "bufferpool.pagefault.count",
            Type: MetricType.Counter,
            Description: "Page faults resulting in on-demand load",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // EVICTIONS / FLUSHES
        // --------------------------
        public static readonly MetricDefinition Evictions = new(
            Name: "bufferpool.eviction.count",
            Type: MetricType.Counter,
            Description: "Eviction events (page removed from pool)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Blocked eviction attempts due to pinned or busy pages
        public static readonly MetricDefinition EvictionBlocked = new(
            Name: "bufferpool.eviction.blocked.count",
            Type: MetricType.Counter,
            Description: "Blocked eviction attempts due to pinned or busy pages",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition Flushes = new(
            Name: "bufferpool.flush.count",
            Type: MetricType.Counter,
            Description: "Writebacks of dirty pages to backing device",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FlushLatencyMs = new(
            Name: "bufferpool.flush.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of page flush operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // I/O COUNTS / LATENCY / BYTES
        // --------------------------
        public static readonly MetricDefinition ReadLatencyMs = new(
            Name: "bufferpool.read.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of underlying device reads",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteLatencyMs = new(
            Name: "bufferpool.write.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of underlying device writes",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesRead = new(
            Name: "bufferpool.read.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes read from backing device",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesWritten = new(
            Name: "bufferpool.write.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes written to backing device",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition IoRetries = new(
            Name: "bufferpool.io.retry.count",
            Type: MetricType.Counter,
            Description: "Retries for failed I/O operations",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // LATCH / CONCURRENCY
        // --------------------------
        public static readonly MetricDefinition LatchWaitMs = new(
            Name: "bufferpool.latch.wait_time_ms",
            Type: MetricType.Histogram,
            Description: "Time spent waiting for per-page latches",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LatchContention = new(
            Name: "bufferpool.latch.contention.count",
            Type: MetricType.Counter,
            Description: "Latch contention events detected",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // WAL (OPTIONAL)
        // --------------------------
        public static readonly MetricDefinition WalAppendLatencyMs = new(
            Name: "bufferpool.wal.append.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of WAL append operations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WalAppendBytes = new(
            Name: "bufferpool.wal.append.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes appended to the WAL",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WalAppendCount = new(
            Name: "bufferpool.wal.append.count",
            Type: MetricType.Counter,
            Description: "Number of WAL appends performed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // BUFFER LIFECYCLE
        // --------------------------
        public static readonly MetricDefinition BuffersReturned = new(
            Name: "bufferpool.buffer.returned.count",
            Type: MetricType.Counter,
            Description: "Buffers returned to the shared pool",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Finalizer path: object was GC-finalized without DisposeAsync being called
        public static readonly MetricDefinition FinalizedWithoutDispose = new(
            Name: "bufferpool.finalized_without_dispose.count",
            Type: MetricType.Counter,
            Description: "BufferPoolManager instances finalized without DisposeAsync being called",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // Finalizer path: number of frames reclaimed when finalized without DisposeAsync
        public static readonly MetricDefinition FinalizerReclaimedFrames = new(
            Name: "bufferpool.finalizer.reclaimed_frames.count",
            Type: MetricType.Counter,
            Description: "Number of page frames reclaimed in finalizer cleanup",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // GAUGES (OBSERVABLE)
        // --------------------------
        public static readonly MetricDefinition ResidentPages = new(
            Name: "bufferpool.pages.resident",
            Type: MetricType.ObservableGauge,
            Description: "Current number of resident pages",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CapacityPages = new(
            Name: "bufferpool.pages.capacity",
            Type: MetricType.ObservableGauge,
            Description: "Configured capacity of the buffer pool (pages), if bounded",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition DirtyPages = new(
            Name: "bufferpool.pages.dirty",
            Type: MetricType.ObservableGauge,
            Description: "Current number of dirty pages",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition PinnedPages = new(
            Name: "bufferpool.pages.pinned",
            Type: MetricType.ObservableGauge,
            Description: "Current number of pinned pages (sum of pages with pin count > 0)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition Utilization = new(
            Name: "bufferpool.utilization",
            Type: MetricType.ObservableGauge,
            Description: "Utilization ratio of the buffer pool (resident/capacity)",
            Unit: "ratio",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition InFlightOps = new(
            Name: "bufferpool.inflight.ops",
            Type: MetricType.ObservableGauge,
            Description: "In-flight buffer pool operations (best-effort)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REASONS (stable, low-cardinality enum-like constants)
        // =====================================================================
        public static class Fields
        {
            public const string Capacity = "capacity";        // evicted due to capacity pressure
            public const string Idle = "idle";                // evicted due to idle/age
            public const string Shutdown = "shutdown";        // eviction/flush due to shutdown
            public const string Error = "error";              // drop/evict due to error paths
            public const string RangeOverlap = "range_overlap"; // latch/write overlap
        }

        // =====================================================================
        // REGISTRATION
        // =====================================================================
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName,
            Func<int>? residentPagesProvider = null,
            Func<int>? capacityPagesProvider = null,
            Func<int>? dirtyPagesProvider = null,
            Func<int>? pinnedPagesProvider = null,
            Func<double>? utilizationProvider = null,
            IInFlightProvider? inFlightProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = "BufferPool";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Counters and histograms
            Reg(Hits);
            Reg(Misses);
            Reg(PageFaults);
            Reg(Evictions);
            Reg(EvictionBlocked);
            Reg(Flushes);
            Reg(FlushLatencyMs);
            Reg(ReadLatencyMs);
            Reg(WriteLatencyMs);
            Reg(BytesRead);
            Reg(BytesWritten);
            Reg(IoRetries);
            Reg(LatchWaitMs);
            Reg(LatchContention);
            Reg(WalAppendLatencyMs);
            Reg(WalAppendBytes);
            Reg(WalAppendCount);
            Reg(BuffersReturned);
            Reg(FinalizedWithoutDispose);
            Reg(FinalizerReclaimedFrames);

            // Gauges, conditionally wired
            if (residentPagesProvider is not null)
            {
                var g = ResidentPages with
                {
                    LongCallback = () =>
                    {
                        var v = residentPagesProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
            if (capacityPagesProvider is not null)
            {
                var g = CapacityPages with
                {
                    LongCallback = () =>
                    {
                        var v = capacityPagesProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
            if (dirtyPagesProvider is not null)
            {
                var g = DirtyPages with
                {
                    LongCallback = () =>
                    {
                        var v = dirtyPagesProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
            if (pinnedPagesProvider is not null)
            {
                var g = PinnedPages with
                {
                    LongCallback = () =>
                    {
                        var v = pinnedPagesProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
            if (utilizationProvider is not null)
            {
                var g = Utilization with
                {
                    DoubleCallback = () =>
                    {
                        var v = utilizationProvider();
                        return new[] { new Measurement<double>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
            if (inFlightProvider is not null)
            {
                var g = InFlightOps with
                {
                    LongCallback = () =>
                    {
                        var v = inFlightProvider.Current;
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // =====================================================================
        // EVENT HELPERS
        // =====================================================================
        public static void IncrementHit(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, Hits);
            try { metrics.Increment(Hits.Name, 1); } catch { }
        }

        public static void IncrementMiss(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, Misses);
            try { metrics.Increment(Misses.Name, 1); } catch { }
        }

        public static void IncrementPageFault(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, PageFaults);
            try { metrics.Increment(PageFaults.Name, 1); } catch { }
        }

        public static void IncrementEviction(IMetricsManager metrics, string? reasonField = null)
        {
            if (metrics is null) return;
            TryRegister(metrics, Evictions);
            try
            {
                if (string.IsNullOrWhiteSpace(reasonField))
                {
                    metrics.Increment(Evictions.Name, 1);
                }
                else
                {
                    metrics.Increment(
                        Evictions.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, reasonField));
                }
            }
            catch { }
        }

        /// <summary>
        /// Record an eviction attempt that was blocked (e.g. page still pinned).
        /// </summary>
        public static void RecordEvictionBlocked(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, EvictionBlocked);
            try { metrics.Increment(EvictionBlocked.Name, 1); }
            catch { }
        }
        /// <summary>
        /// Record an eviction error (e.g. exception in background eviction/flush).
        /// Emits an Evictions counter using the 'error' reason tag.
        /// </summary>
        public static void RecordEvictionError(IMetricsManager metrics, Exception _)
        {
            if (metrics is null) return;
            // Ensure the Evictions metric is registered
            TryRegister(metrics, Evictions);
            try
            {
                // Increment with Field=error
                metrics.Increment(
                    Evictions.Name,
                    1,
                    new KeyValuePair<string, object>(TagKeys.Field, Fields.Error));
            }
            catch
            {
                // Swallow to never impact hot paths
            }
        }

        public static void OnFlushCompleted(IMetricsManager metrics, double latencyMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, Flushes);
            TryRegister(metrics, FlushLatencyMs);
            try
            {
                metrics.Increment(Flushes.Name, 1);
                metrics.Record(FlushLatencyMs.Name, latencyMs);
            }
            catch { }
        }

        public static void OnReadCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, ReadLatencyMs);
            TryRegister(metrics, BytesRead);
            try
            {
                metrics.Record(ReadLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesRead.Name, bytes);
            }
            catch { }
        }

        public static void OnWriteCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, WriteLatencyMs);
            TryRegister(metrics, BytesWritten);
            try
            {
                metrics.Record(WriteLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesWritten.Name, bytes);
            }
            catch { }
        }

        public static void IncrementIoRetry(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, IoRetries);
            try { metrics.Increment(IoRetries.Name, 1); } catch { }
        }

        public static void RecordLatchWaitMs(IMetricsManager metrics, double waitMs, string? reasonField = null)
        {
            if (metrics is null) return;
            TryRegister(metrics, LatchWaitMs);
            TryRegister(metrics, LatchContention);
            try
            {
                metrics.Record(LatchWaitMs.Name, waitMs);
                metrics.Increment(
                    LatchContention.Name,
                    1,
                    string.IsNullOrWhiteSpace(reasonField)
                        ? default
                        : new KeyValuePair<string, object>(TagKeys.Field, reasonField)
                );
            }
            catch
            {
                // Swallow exceptions to avoid impacting hot paths
            }
        }

        public static void RecordWalAppend(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, WalAppendLatencyMs);
            TryRegister(metrics, WalAppendBytes);
            TryRegister(metrics, WalAppendCount);
            try
            {
                metrics.Record(WalAppendLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(WalAppendBytes.Name, bytes);
                metrics.Increment(WalAppendCount.Name, 1);
            }
            catch
            {
                // Swallow exceptions to avoid impacting hot paths
            }
        }

        public static void RecordBufferReturned(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, BuffersReturned);
            try { metrics.Increment(BuffersReturned.Name, 1); } catch { }
        }

        /// <summary>
        /// Record that a BufferPoolManager was finalized without DisposeAsync being called.
        /// </summary>
        public static void RecordFinalizedWithoutDispose(IMetricsManager metrics, string componentName)
        {
            if (metrics is null) return;
            var def = FinalizedWithoutDispose with
            {
                DefaultTags = new[] { new KeyValuePair<string, object>(TagKeys.Component, componentName) }
            };
            TryRegister(metrics, def);
            try { metrics.Increment(FinalizedWithoutDispose.Name, 1); }
            catch { /* swallow to avoid impacting finalizer thread */ }
        }

        /// <summary>
        /// Record that N frames were reclaimed in finalizer cleanup.
        /// </summary>
        public static void RecordFinalizerReclaimedFrames(IMetricsManager metrics, int count, string componentName)
        {
            if (metrics is null) return;
            var def = FinalizerReclaimedFrames with
            {
                DefaultTags = new[] { new KeyValuePair<string, object>(TagKeys.Component, componentName) }
            };
            TryRegister(metrics, def);
            try { metrics.Increment(FinalizerReclaimedFrames.Name, count); }
            catch { /* swallow to avoid impacting finalizer thread */ }
        }
        // =====================================================================
        // INTERNAL SAFE REGISTRATION
        // =====================================================================
        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try
            {
                metrics.Register(def);
            }
            catch
            {
                // Ignore duplicate registration or other non-fatal errors
            }
        }
    }

    /// <summary>
    /// Optional interface for providing in-flight operation counts to gauges.
    /// </summary>
    public interface IInFlightProvider
    {
        long Current { get; }
    }
}
