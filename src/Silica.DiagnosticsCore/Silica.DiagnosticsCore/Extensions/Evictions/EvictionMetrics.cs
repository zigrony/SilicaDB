// File: Silica.DiagnosticsCore/Extensions/Evictions/EvictionMetrics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Extensions.Evictions
{
    /// <summary>
    /// Canonical, low-cardinality Evictions metric definitions and helpers for DiagnosticsCore.
    /// Use this contract from any cache/eviction implementation (LRU, MRU, LFU, CLOCK, TTL).
    /// </summary>
    /// <remarks>
    /// Design goals:
    /// - Contract-first: stable names, types, units, and allowed tags.
    /// - Low cardinality: only TagKeys.Component as a default tag; reasons go into TagKeys.Field with a stable set.
    /// - No LINQ/reflection/JSON/3rd-party libs.
    /// - Safe in strict metrics mode; tolerant of multiple registrations across restarts.
    /// </remarks>
    public static class EvictionMetrics
    {
        // --------------------------
        // HIT / MISS
        // --------------------------
        public static readonly MetricDefinition CacheHitCount = new(
            Name: "evictions.cache.hit.count",
            Type: MetricType.Counter,
            Description: "Cache hits (GetOrAdd found existing)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CacheMissCount = new(
            Name: "evictions.cache.miss.count",
            Type: MetricType.Counter,
            Description: "Cache misses (GetOrAdd needed factory)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // EVICTIONS
        // --------------------------
        public static readonly MetricDefinition EvictionCount = new(
            Name: "evictions.cache.eviction.count",
            Type: MetricType.Counter,
            Description: "Eviction events (capacity, idle, policy driven)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition EvictionDurationMs = new(
            Name: "evictions.cache.eviction.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of individual eviction executions",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // FACTORY / CREATION (miss work)
        // --------------------------
        public static readonly MetricDefinition FactoryLatencyMs = new(
            Name: "evictions.cache.factory.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of value factory execution for misses",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CLEANUP
        // --------------------------
        public static readonly MetricDefinition CleanupCount = new(
            Name: "evictions.cache.cleanup.count",
            Type: MetricType.Counter,
            Description: "Cleanup runs (e.g., idle sweeps) that completed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CleanupDurationMs = new(
            Name: "evictions.cache.cleanup.duration_ms",
            Type: MetricType.Histogram,
            Description: "Duration of cleanup runs",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // OPTIONAL GAUGES (OBSERVABLE)
        // --------------------------
        public static readonly MetricDefinition EntriesGauge = new(
            Name: "evictions.cache.entries",
            Type: MetricType.ObservableGauge,
            Description: "Current number of entries held by the cache",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition CapacityGauge = new(
            Name: "evictions.cache.capacity",
            Type: MetricType.ObservableGauge,
            Description: "Configured maximum capacity of the cache (if bounded)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // CLOCK SUPPORT (OPTIONAL)
        // --------------------------
        public static readonly MetricDefinition ClockSecondChanceClears = new(
            Name: "evictions.cache.clock.second_chance.clears",
            Type: MetricType.Counter,
            Description: "Number of times CLOCK cleared reference bits (second-chance)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REASONS (stable, low-cardinality enum-like constants)
        // =====================================================================
        public static class Fields
        {
            public const string Capacity = "capacity";
            public const string Idle = "idle";
            public const string Lru = "lru";
            public const string Mru = "mru";
            public const string Lfu = "lfu";
            public const string Clock = "clock";
            public const string SizeOnly = "size_only";
            public const string Time = "time";
        }

        // =====================================================================
        // REGISTRATION
        // =====================================================================
        public static void RegisterAll(
            IMetricsManager metrics,
            string cacheComponentName,
            Func<int>? entriesProvider = null,
            Func<int>? capacityProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(cacheComponentName))
                cacheComponentName = "EvictionCache";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, cacheComponentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(CacheHitCount);
            Reg(CacheMissCount);
            Reg(EvictionCount);
            Reg(EvictionDurationMs);
            Reg(FactoryLatencyMs);
            Reg(CleanupCount);
            Reg(CleanupDurationMs);
            Reg(ClockSecondChanceClears);

            if (entriesProvider is not null)
            {
                var g = EntriesGauge with
                {
                    LongCallback = () =>
                    {
                        var v = entriesProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }

            if (capacityProvider is not null)
            {
                var g = CapacityGauge with
                {
                    LongCallback = () =>
                    {
                        var v = capacityProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // =====================================================================
        // HELPERS
        // =====================================================================
        public static void IncrementHit(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, CacheHitCount);
            try { metrics.Increment(CacheHitCount.Name, 1); } catch { }
        }

        public static void IncrementMiss(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, CacheMissCount);
            try { metrics.Increment(CacheMissCount.Name, 1); } catch { }
        }

        public static void IncrementEviction(IMetricsManager metrics, string? reasonField = null)
        {
            if (metrics is null) return;
            TryRegister(metrics, EvictionCount);
            try
            {
                if (string.IsNullOrWhiteSpace(reasonField))
                {
                    metrics.Increment(EvictionCount.Name, 1);
                }
                else
                {
                    metrics.Increment(
                        EvictionCount.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, reasonField));
                }
            }
            catch { }
        }

        public static void RecordEvictionDuration(IMetricsManager metrics, double durationMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, EvictionDurationMs);
            try { metrics.Record(EvictionDurationMs.Name, durationMs); } catch { }
        }

        public static void RecordFactoryLatency(IMetricsManager metrics, double latencyMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, FactoryLatencyMs);
            try { metrics.Record(FactoryLatencyMs.Name, latencyMs); } catch { }
        }

        public static void OnCleanupCompleted(IMetricsManager metrics, double durationMs)
        {
            if (metrics is null) return;
            TryRegister(metrics, CleanupCount);
            TryRegister(metrics, CleanupDurationMs);
            try
            {
                metrics.Increment(CleanupCount.Name, 1);
                metrics.Record(CleanupDurationMs.Name, durationMs);
            }
            catch { }
        }

        public static void IncrementClockSecondChanceClears(IMetricsManager metrics, long count = 1)
        {
            if (metrics is null) return;
            if (count <= 0) return;
            TryRegister(metrics, ClockSecondChanceClears);
            try { metrics.Increment(ClockSecondChanceClears.Name, count); } catch { }
        }

        // -----------------------------------------------------------------
        // INTERNAL
        // -----------------------------------------------------------------
        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            if (metrics is MetricsFacade mf)
            {
                try
                {
                    mf.TryRegister(def);
                }
                catch
                {
                    // Swallow to avoid surfacing registration errors to hot paths
                }
            }
            else
            {
                try
                {
                    metrics.Register(def);
                }
                catch
                {
                    // Swallow to avoid surfacing registration errors to hot paths
                }
            }
        }
    }
}
