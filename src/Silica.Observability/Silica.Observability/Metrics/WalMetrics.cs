// File: WalMetrics.cs
// Namespace: SilicaDB.Metrics
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.Observability.Metrics.Interfaces;

namespace Silica.Observability.Metrics
{
    /// <summary>
    /// Default, built-in metrics for WalManager. Mirrors the StorageMetrics style:
    /// counters for operation counts, histograms for latency/bytes, up-down for inflight,
    /// and an observable for the latest LSN.
    /// </summary>
    public static class WalMetrics
    {
        private const string DeviceKey = "device";
        private const string OperationKey = "operation";

        // Lifecycle
        public static readonly MetricDefinition StartCount =
            new MetricDefinition(
                name: "wal.start.count",
                type: MetricType.Counter,
                description: "Number of times WAL.StartAsync was called",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition StartDuration =
            new MetricDefinition(
                name: "wal.start.duration",
                type: MetricType.Histogram,
                description: "Duration of WAL.StartAsync (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition StopCount =
            new MetricDefinition(
                name: "wal.stop.count",
                type: MetricType.Counter,
                description: "Number of times WAL.StopAsync was called",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition StopDuration =
            new MetricDefinition(
                name: "wal.stop.duration",
                type: MetricType.Histogram,
                description: "Duration of WAL.StopAsync (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        // Append path
        public static readonly MetricDefinition AppendCount =
            new MetricDefinition(
                name: "wal.append.count",
                type: MetricType.Counter,
                description: "Total append operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "append")
                }
            );

        public static readonly MetricDefinition AppendDuration =
            new MetricDefinition(
                name: "wal.append.duration",
                type: MetricType.Histogram,
                description: "Append latency (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "append")
                }
            );

        public static readonly MetricDefinition BytesAppended =
            new MetricDefinition(
                name: "wal.append.bytes",
                type: MetricType.Histogram,
                description: "Bytes appended to WAL",
                unit: "bytes",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "append")
                }
            );

        // Flush path
        public static readonly MetricDefinition FlushCount =
            new MetricDefinition(
                name: "wal.flush.count",
                type: MetricType.Counter,
                description: "Total flush operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "flush")
                }
            );

        public static readonly MetricDefinition FlushDuration =
            new MetricDefinition(
                name: "wal.flush.duration",
                type: MetricType.Histogram,
                description: "Flush latency (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}"),
                    (OperationKey, "flush")
                }
            );

        // Contention / concurrency
        public static readonly MetricDefinition InFlightOps =
            new MetricDefinition(
                name: "wal.inflight",
                type: MetricType.UpDownCounter,
                description: "Number of concurrent in-flight WAL operations",
                unit: "ops",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        public static readonly MetricDefinition LockWaitTime =
            new MetricDefinition(
                name: "wal.lock.wait",
                type: MetricType.Histogram,
                description: "Time waiting for WAL write lock (ms)",
                unit: "ms",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        // Health / progress
        public static readonly MetricDefinition LatestLsn =
            new MetricDefinition(
                name: "wal.lsn.latest",
                type: MetricType.ObservableUpDownCounter,
                description: "Most recent assigned WAL sequence number",
                unit: "seq",
                defaultTags: new (string Key, object Value)[]
                {
                    (DeviceKey, "{deviceName}")
                }
            );

        /// <summary>
        /// Register all WAL metrics for a given WAL instance, resolving the "{deviceName}" tag.
        /// Optionally wire a live observable for LatestLsn via <paramref name="lastLsnProvider"/>.
        /// </summary>
        public static void RegisterAll(
            IMetricsManager mgr,
            string walName,
            Func<long>? lastLsnProvider = null)
        {
            foreach (var def in new[]
            {
                StartCount,     StartDuration,
                StopCount,      StopDuration,
                AppendCount,    AppendDuration, BytesAppended,
                FlushCount,     FlushDuration,
                InFlightOps,    LockWaitTime,
                LatestLsn
            })
            {
                // Reuse the device-tag placeholder mechanism for the WAL name
                def.ResolveDeviceTag(walName);

                if (def == LatestLsn && lastLsnProvider is not null)
                {
                    def.LongObservableCallback = () => new[]
                    {
                        new Measurement<long>(
                            lastLsnProvider(),
                            new KeyValuePair<string, object>[]
                            {
                                new(DeviceKey, walName)
                            })
                    };
                }

                mgr.Register(def);
            }
        }
    }
}
