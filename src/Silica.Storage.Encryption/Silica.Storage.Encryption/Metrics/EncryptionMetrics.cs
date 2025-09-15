using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Storage.Encryption.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality Encryption metric definitions and helpers for DiagnosticsCore.
    /// Contract-first: stable names, types, units, and allowed tags.
    /// No LINQ/reflection/JSON/third-party libs.
    /// </summary>
    public static class EncryptionMetrics
    {
        // --------------------------
        // CORE TRANSFORM METRICS
        // --------------------------

        public static readonly MetricDefinition EncryptLatencyMs = new(
            Name: "encryption.encrypt.latency_ms",
            Type: MetricType.Histogram,
            Description: "Encryption latency distribution (AES-CTR transform + overhead at call site)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition DecryptLatencyMs = new(
            Name: "encryption.decrypt.latency_ms",
            Type: MetricType.Histogram,
            Description: "Decryption latency distribution (AES-CTR transform + overhead at call site)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesEncrypted = new(
            Name: "encryption.encrypt.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes encrypted via AES-CTR",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesDecrypted = new(
            Name: "encryption.decrypt.bytes",
            Type: MetricType.Histogram,
            Description: "Bytes decrypted via AES-CTR",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // ERROR/RETRY INDICATORS
        // --------------------------

        public static readonly MetricDefinition TransformFailureCount = new(
            Name: "encryption.transform.failure.count",
            Type: MetricType.Counter,
            Description: "AES-CTR transform failures (e.g., disposed/invalid state/mismatched buffers/internal crypto faults)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WrongKeyOrSaltCount = new(
            Name: "encryption.wrong_key_or_salt.count",
            Type: MetricType.Counter,
            Description: "Count of decryption attempts that produced plaintext mismatch due to wrong key/salt",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ValidationFailureCount = new(
            Name: "encryption.validation.failure.count",
            Type: MetricType.Counter,
            Description: "Configuration/validation failures (invalid key length, invalid salt length, null provider/values, counter length)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION
        // =====================================================================

        /// <summary>
        /// Registers all Encryption metric definitions with the metrics manager.
        /// Tag set is low-cardinality and uses TagKeys.Component bound to the device component name.
        /// </summary>
        public static void RegisterAll(
            IMetricsManager metrics,
            string deviceComponentName)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(deviceComponentName))
                deviceComponentName = "EncryptionDevice";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, deviceComponentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Core transforms
            Reg(EncryptLatencyMs);
            Reg(DecryptLatencyMs);
            Reg(BytesEncrypted);
            Reg(BytesDecrypted);

            // Errors/retries
            Reg(TransformFailureCount);
            Reg(WrongKeyOrSaltCount);
            Reg(ValidationFailureCount);
        }

        // =====================================================================
        // HELPERS (EVENT EMISSION)
        // =====================================================================

        public static void OnEncryptCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, EncryptLatencyMs);
            TryRegister(metrics, BytesEncrypted);
            try
            {
                metrics.Record(EncryptLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesEncrypted.Name, bytes);
            }
            catch { }
        }

        public static void OnDecryptCompleted(IMetricsManager metrics, double latencyMs, long bytes)
        {
            if (metrics is null) return;
            TryRegister(metrics, DecryptLatencyMs);
            TryRegister(metrics, BytesDecrypted);
            try
            {
                metrics.Record(DecryptLatencyMs.Name, latencyMs);
                if (bytes > 0) metrics.Record(BytesDecrypted.Name, bytes);
            }
            catch { }
        }

        public static void IncrementTransformFailure(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, TransformFailureCount);
            try { metrics.Increment(TransformFailureCount.Name, 1); } catch { }
        }

        public static void IncrementWrongKeyOrSalt(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, WrongKeyOrSaltCount);
            try { metrics.Increment(WrongKeyOrSaltCount.Name, 1); } catch { }
        }

        public static void IncrementValidationFailure(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, ValidationFailureCount);
            try { metrics.Increment(ValidationFailureCount.Name, 1); } catch { }
        }

        // =====================================================================
        // INTERNAL
        // =====================================================================

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            if (metrics is MetricsFacade mf)
            {
                try { mf.TryRegister(def); } catch { }
            }
            else
            {
                try { metrics.Register(def); } catch { }
            }
        }
    }
}
