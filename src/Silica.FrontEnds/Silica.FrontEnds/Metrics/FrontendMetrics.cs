// File: Silica.FrontEnds/Metrics/FrontendMetrics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.FrontEnds.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality frontend metric definitions and helpers.
    /// Contract-first: stable names, units, and semantics.
    /// </summary>
    public static class FrontendMetrics
    {
        public static readonly MetricDefinition BindCount = new(
            Name: "frontends.bind.count",
            Type: MetricType.Counter,
            Description: "Frontends successfully bound (e.g., ports, protocols)",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FailureCount = new(
            Name: "frontends.failure.count",
            Type: MetricType.Counter,
            Description: "Failures during frontend configuration/bind",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ActiveGauge = new(
            Name: "frontends.active.count",
            Type: MetricType.ObservableGauge,
            Description: "Approximate number of active frontends",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static class Fields
        {
            public const string Frontend = "frontend"; // e.g., kestrel, grpc, websocket
            public const string Endpoint = "endpoint"; // e.g., 0.0.0.0:5001
            public const string Outcome = "outcome";   // ok / error
        }

        public static void RegisterAll(IMetricsManager metrics, string componentName, Func<long>? activeCountProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "Silica.FrontEnds";
            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(BindCount);
            Reg(FailureCount);

            if (activeCountProvider is not null)
            {
                var g = ActiveGauge with
                {
                    LongCallback = () =>
                    {
                        long v = 0;
                        try { v = activeCountProvider(); } catch { }
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // Note: Observable gauge should be registered centrally to avoid duplicate callbacks
        // when multiple frontends are constructed. Prefer registry-level registration.

        public static void IncrementBind(IMetricsManager metrics, string frontend, string endpoint)
        {
            if (metrics is null) return;
            TryRegister(metrics, BindCount);
            metrics.Increment(BindCount.Name, 1,
                new KeyValuePair<string, object>(Fields.Frontend, frontend),
                new KeyValuePair<string, object>(Fields.Endpoint, endpoint),
                // Explicit outcome parity for dashboards
                new KeyValuePair<string, object>(Fields.Outcome, "ok"));
        }

        public static void IncrementFailure(IMetricsManager metrics, string frontend, string endpoint)
        {
            if (metrics is null) return;
            TryRegister(metrics, FailureCount);
            metrics.Increment(FailureCount.Name, 1,
                new KeyValuePair<string, object>(Fields.Frontend, frontend),
                new KeyValuePair<string, object>(Fields.Endpoint, endpoint),
                new KeyValuePair<string, object>(Fields.Outcome, "error"));
        }

        // Overloads that include component for explicit tagging when default-tags aren’t present
        public static void IncrementBind(IMetricsManager metrics, string componentName, string frontend, string endpoint)
        {
            if (metrics is null) return;
            TryRegister(metrics, BindCount);
            KeyValuePair<string, object> compTag;
            if (string.IsNullOrWhiteSpace(componentName))
                compTag = new KeyValuePair<string, object>(TagKeys.Component, "Silica.FrontEnds");
            else
                compTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            metrics.Increment(BindCount.Name, 1,
                compTag,
                new KeyValuePair<string, object>(Fields.Frontend, frontend),
                new KeyValuePair<string, object>(Fields.Endpoint, endpoint),
                new KeyValuePair<string, object>(Fields.Outcome, "ok"));
        }

        public static void IncrementFailure(IMetricsManager metrics, string componentName, string frontend, string endpoint)
        {
            if (metrics is null) return;
            TryRegister(metrics, FailureCount);
            KeyValuePair<string, object> compTag;
            if (string.IsNullOrWhiteSpace(componentName))
                compTag = new KeyValuePair<string, object>(TagKeys.Component, "Silica.FrontEnds");
            else
                compTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            metrics.Increment(FailureCount.Name, 1,
                compTag,
                new KeyValuePair<string, object>(Fields.Frontend, frontend),
                new KeyValuePair<string, object>(Fields.Endpoint, endpoint),
                new KeyValuePair<string, object>(Fields.Outcome, "error"));
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
