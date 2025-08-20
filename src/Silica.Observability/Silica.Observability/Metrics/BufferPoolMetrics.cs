using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.Observability.Metrics.Interfaces;

namespace Silica.Observability.Metrics
{
    public static class BufferPoolMetrics
    {
        private const string PoolKey = "pool";

        // Count of buffer pool read hits
        public static readonly MetricDefinition Hits = new(
            name: "buffer.hits",
            type: MetricType.Counter,
            description: "Number of times a page read hit in the buffer pool",
            unit: "ops",
            defaultTags: new (string, object)[]
            {
                (PoolKey, "{poolName}")
            }
        );

        // Count of buffer pool read misses (page loads)
        public static readonly MetricDefinition Misses = new(
            name: "buffer.misses",
            type: MetricType.Counter,
            description: "Number of times a page read missed and was loaded from device",
            unit: "ops",
            defaultTags: new (string, object)[]
            {
                (PoolKey, "{poolName}")
            }
        );

        // Count of flush operations (writes through WAL/device)
        public static readonly MetricDefinition Flushes = new(
            name: "buffer.flushes",
            type: MetricType.Counter,
            description: "Number of times a dirty page was flushed to the device",
            unit: "ops",
            defaultTags: new (string, object)[]
            {
                (PoolKey, "{poolName}")
            }
        );

        // Count of successful evictions from the buffer pool
        public static readonly MetricDefinition Evictions = new(
            name: "buffer.evictions",
            type: MetricType.Counter,
            description: "Number of pages evicted from the buffer pool",
            unit: "ops",
            defaultTags: new (string, object)[]
            {
                (PoolKey, "{poolName}")
            }
        );

        // Gauge of the current number of frames (pages) resident in the pool
        public static readonly MetricDefinition FramesResident = new(
            name: "buffer.frames.resident",
            type: MetricType.ObservableGauge,
            description: "Current count of pages resident in the buffer pool",
            unit: "pages",
            defaultTags: new (string, object)[]
            {
                (PoolKey, "{poolName}")
            }
        );

        /// <summary>
        /// Register all buffer-pool metrics, resolving the "{poolName}" placeholder and
        /// wiring up the live gauge callback for resident frame count.
        /// </summary>
        public static void RegisterAll(
            IMetricsManager mgr,
            string poolName,
            Func<double> framesCountProvider)
        {
            // Resolve "{poolName}" in each definition's default tags
            void ResolvePoolTag(MetricDefinition def)
            {
                var tags = def.DefaultTags;
                for (int i = 0; i < tags.Length; i++)
                {
                    if (tags[i].Key == PoolKey && tags[i].Value is string s && s == "{poolName}")
                    {
                        tags[i] = new KeyValuePair<string, object>(PoolKey, poolName);
                    }
                }
            }

            // List all definitions in the order we want to register them
            var defs = new[]
            {
                Hits,
                Misses,
                Flushes,
                Evictions,
                FramesResident
            };

            foreach (var def in defs)
            {
                ResolvePoolTag(def);

                if (def == FramesResident)
                {
                    // Wire up the live callback for the gauge
                    def.DoubleObservableCallback = () => new[]
                    {
                        new Measurement<double>(
                            framesCountProvider(),
                            new KeyValuePair<string, object>[]
                            {
                                new KeyValuePair<string, object>(PoolKey, poolName)
                            })
                    };
                }

                mgr.Register(def);
            }
        }
    }
}
