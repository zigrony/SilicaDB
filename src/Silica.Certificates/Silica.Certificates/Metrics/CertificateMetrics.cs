using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Certificates.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality certificate metric definitions and helpers.
    /// Contract-first: stable names, units, and semantics.
    /// </summary>
    public static class CertificateMetrics
    {
        // --------------------------
        // LIFECYCLE
        // --------------------------
        public static readonly MetricDefinition GeneratedCount = new(
            Name: "certificates.generated.count",
            Type: MetricType.Counter,
            Description: "Certificates generated (e.g., ephemeral, self-signed, ACME, BYO)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LoadedCount = new(
            Name: "certificates.loaded.count",
            Type: MetricType.Counter,
            Description: "Certificates loaded (configuration, store, file, secret manager)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ExportedCount = new(
            Name: "certificates.exported.count",
            Type: MetricType.Counter,
            Description: "Certificates exported to PFX/DER/PEM for consumption",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BoundEndpointCount = new(
            Name: "certificates.bound.endpoint.count",
            Type: MetricType.Counter,
            Description: "Certificates successfully bound to TLS endpoints (e.g., Kestrel)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition RenewedCount = new(
            Name: "certificates.renewed.count",
            Type: MetricType.Counter,
            Description: "Certificates renewed (rotation or ACME)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition PrunedCount = new(
            Name: "certificates.pruned.count",
            Type: MetricType.Counter,
            Description: "Certificates pruned/evicted from active cache/state",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FailureCount = new(
            Name: "certificates.failure.count",
            Type: MetricType.Counter,
            Description: "Failures during certificate operations (generate/load/export/bind/renew)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // DISTRIBUTIONS
        // --------------------------
        public static readonly MetricDefinition LifetimeDays = new(
            Name: "certificates.lifetime.days",
            Type: MetricType.Histogram,
            Description: "Certificate validity period (NotBefore→NotAfter) in days",
            Unit: "days",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition GenerationLatencyMs = new(
            Name: "certificates.generate.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency to generate a certificate",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // OBSERVABLES
        // --------------------------
        public static readonly MetricDefinition ActiveGauge = new(
            Name: "certificates.active.count",
            Type: MetricType.ObservableGauge,
            Description: "Approximate number of active certificates (per component/provider)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TAG FIELDS (low cardinality)
        // --------------------------
        public static class Fields
        {
            public const string Provider = "provider";      // e.g., ephemeral/selfsigned/acme/byo/store
            public const string Source = "source";          // e.g., config, store, file, secret
            public const string Format = "format";          // e.g., pfx, der, pem
            public const string Endpoint = "endpoint";      // e.g., kestrel, http.sys
            public const string Outcome = "outcome";        // ok / error
        }

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName,
            Func<long>? activeCountProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "Silica.Certificates";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(GeneratedCount);
            Reg(LoadedCount);
            Reg(ExportedCount);
            Reg(BoundEndpointCount);
            Reg(RenewedCount);
            Reg(PrunedCount);
            Reg(FailureCount);
            Reg(LifetimeDays);
            Reg(GenerationLatencyMs);

            if (activeCountProvider is not null)
            {
                var g = ActiveGauge with
                {
                    LongCallback = () =>
                    {
                        var v = activeCountProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // --------------------------
        // HELPERS
        // --------------------------
        public static void IncrementGenerated(IMetricsManager metrics, string provider)
        {
            if (metrics is null) return;
            TryRegister(metrics, GeneratedCount);
            metrics.Increment(GeneratedCount.Name, 1, new KeyValuePair<string, object>(Fields.Provider, provider));
        }

        public static void IncrementLoaded(IMetricsManager metrics, string source)
        {
            if (metrics is null) return;
            TryRegister(metrics, LoadedCount);
            metrics.Increment(LoadedCount.Name, 1, new KeyValuePair<string, object>(Fields.Source, source));
        }

        public static void IncrementExported(IMetricsManager metrics, string format)
        {
            if (metrics is null) return;
            TryRegister(metrics, ExportedCount);
            metrics.Increment(ExportedCount.Name, 1, new KeyValuePair<string, object>(Fields.Format, format));
        }

        public static void IncrementBoundEndpoint(IMetricsManager metrics, string endpoint)
        {
            if (metrics is null) return;
            TryRegister(metrics, BoundEndpointCount);
            metrics.Increment(BoundEndpointCount.Name, 1, new KeyValuePair<string, object>(Fields.Endpoint, endpoint));
        }

        public static void IncrementRenewed(IMetricsManager metrics, string provider)
        {
            if (metrics is null) return;
            TryRegister(metrics, RenewedCount);
            metrics.Increment(RenewedCount.Name, 1, new KeyValuePair<string, object>(Fields.Provider, provider));
        }

        public static void IncrementPruned(IMetricsManager metrics, int count)
        {
            if (metrics is null) return;
            if (count <= 0) return;
            TryRegister(metrics, PrunedCount);
            metrics.Increment(PrunedCount.Name, count);
        }

        public static void IncrementFailure(IMetricsManager metrics, string operation)
        {
            if (metrics is null) return;
            TryRegister(metrics, FailureCount);
            metrics.Increment(FailureCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Operation, operation));
        }

        public static void RecordLifetimeDays(IMetricsManager metrics, double days, string provider)
        {
            if (metrics is null) return;
            TryRegister(metrics, LifetimeDays);
            metrics.Record(LifetimeDays.Name, days, new KeyValuePair<string, object>(Fields.Provider, provider));
        }

        public static void RecordGenerationLatencyMs(IMetricsManager metrics, double ms, string provider)
        {
            if (metrics is null) return;
            TryRegister(metrics, GenerationLatencyMs);
            metrics.Record(GenerationLatencyMs.Name, ms, new KeyValuePair<string, object>(Fields.Provider, provider));
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
