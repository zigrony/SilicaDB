using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.Observability.Metrics;
using Silica.Observability.Metrics.Interfaces;

namespace Silica.Observability.Metrics
{
    public static class StorageMetrics
    {
        private const string DeviceKey = "device";
        private const string OperationKey = "operation";

        public static readonly MetricDefinition MountCount =
            new MetricDefinition(
                name: "storage.mount.count",
                type: MetricType.Counter,
                description: "Number of times storage.MountAsync was called",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition MountDuration =
            new MetricDefinition(
                name: "storage.mount.duration",
                type: MetricType.Histogram,
                description: "Duration of storage.MountAsync (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition ReadCount =
            new MetricDefinition(
                name: "storage.read.count",
                type: MetricType.Counter,
                description: "Total read operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "read")
                }
            );

        public static readonly MetricDefinition ReadLatency =
            new MetricDefinition(
                name: "storage.read.duration",
                type: MetricType.Histogram,
                description: "Read latency (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "read")
                }
            );

        public static readonly MetricDefinition BytesRead =
            new MetricDefinition(
                name: "storage.read.bytes",
                type: MetricType.Histogram,
                description: "Bytes read",
                unit: "bytes",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "read")
                }
            );

        public static readonly MetricDefinition WriteCount =
            new MetricDefinition(
                name: "storage.write.count",
                type: MetricType.Counter,
                description: "Total write operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "write")
                }
            );

        public static readonly MetricDefinition WriteLatency =
            new MetricDefinition(
                name: "storage.write.duration",
                type: MetricType.Histogram,
                description: "Write latency (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "write")
                }
            );

        public static readonly MetricDefinition BytesWritten =
            new MetricDefinition(
                name: "storage.write.bytes",
                type: MetricType.Histogram,
                description: "Bytes written",
                unit: "bytes",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "write")
                }
            );

        public static readonly MetricDefinition InFlightOps =
            new MetricDefinition(
                name: "storage.inflight",
                type: MetricType.UpDownCounter,
                description: "Number of concurrent in-flight operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition LockCacheSize =
            new MetricDefinition(
                name: "storage.lock.cache.size",
                type: MetricType.ObservableGauge,
                description: "Current count of frame locks in cache",
                unit: "locks",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        /// <summary>
        /// Register all storage-related metrics in one shot, resolving the "{deviceName}" tag
        /// and wiring up the live gauge callback for the lock-cache size.
        /// </summary>
        public static void RegisterAll(
            IMetricsManager mgr,
            string deviceName,
            Func<double> cacheSizeProvider)
        {
            foreach (var def in new[]
            {
                MountCount,     MountDuration,
                UnmountCount,   UnmountDuration,           // ← newly added
                ReadCount,      ReadLatency,  BytesRead,
                WriteCount,     WriteLatency, BytesWritten,
                InFlightOps,    LockWaitTime,               // ← newly added
                LockCacheSize,  EvictionCount
            })
            {
                def.ResolveDeviceTag(deviceName);

                if (def == LockCacheSize)
                {
                    def.DoubleObservableCallback = () => new[]
                    {
                        new Measurement<double>(
                            cacheSizeProvider(),
                            new KeyValuePair<string, object>[]
                            {
                                new KeyValuePair<string, object>(DeviceKey, deviceName)
                            })
                    };
                }

                mgr.Register(def);
            }
        }
        public static readonly MetricDefinition EvictionCount =
            new MetricDefinition(
                name: "storage.lock.evicted.count",
                type: MetricType.Counter,
                description: "Number of frame-lock evictions",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
            (DeviceKey, "{deviceName}")
                }
            );
        // Tracks how long callers wait on the frame‐lock semaphore
        public static readonly MetricDefinition LockWaitTime =
            new MetricDefinition(
                name: "storage.lock.wait",
                type: MetricType.Histogram,
                description: "Time waiting for frame lock (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                (DeviceKey, "{deviceName}")
                }
            );

        // Tracks UnmountAsync calls
        public static readonly MetricDefinition UnmountCount =
            new MetricDefinition(
                name: "storage.unmount.count",
                type: MetricType.Counter,
                description: "Number of times storage.UnmountAsync was called",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                (DeviceKey, "{deviceName}")
                }
            );

        // Tracks how long UnmountAsync takes
        public static readonly MetricDefinition UnmountDuration =
            new MetricDefinition(
                name: "storage.unmount.duration",
                type: MetricType.Histogram,
                description: "Duration of storage.UnmountAsync (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                (DeviceKey, "{deviceName}")
                }
            );

    }
}
