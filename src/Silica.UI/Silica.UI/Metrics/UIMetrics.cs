// Filename: Silica.UI/Metrics/UiMetrics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.UI.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality UI metric definitions and helpers.
    /// Mirrors ConcurrencyMetrics design: contract-first, safe, and reusable.
    /// </summary>
    public static class UiMetrics
    {
        // --------------------------
        // AUTH / SESSION
        // --------------------------
        public static readonly MetricDefinition LoginAttemptCount = new(
            Name: "ui.auth.login.attempt.count",
            Type: MetricType.Counter,
            Description: "User login attempts",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LoginFailureCount = new(
            Name: "ui.auth.login.failure.count",
            Type: MetricType.Counter,
            Description: "Failed login attempts",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionResumeLatencyMs = new(
            Name: "ui.session.resume.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency to resume a session",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // FRONTEND
        // --------------------------
        public static readonly MetricDefinition PageLoadLatencyMs = new(
            Name: "ui.page.load.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of UI page loads",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ComponentRenderLatencyMs = new(
            Name: "ui.component.render.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of component rendering",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll(IMetricsManager metrics, string componentName)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "UI";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(LoginAttemptCount);
            Reg(LoginFailureCount);
            Reg(SessionResumeLatencyMs);
            Reg(PageLoadLatencyMs);
            Reg(ComponentRenderLatencyMs);
        }

        // --------------------------
        // HELPERS
        // --------------------------
        public static void IncrementLoginAttempt(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, LoginAttemptCount);
            metrics.Increment(LoginAttemptCount.Name, 1);
        }

        public static void IncrementLoginFailure(IMetricsManager metrics)
        {
            if (metrics is null) return;
            TryRegister(metrics, LoginFailureCount);
            metrics.Increment(LoginFailureCount.Name, 1);
        }

        public static void RecordSessionResumeLatency(IMetricsManager metrics, double ms)
        {
            if (metrics is null) return;
            TryRegister(metrics, SessionResumeLatencyMs);
            metrics.Record(SessionResumeLatencyMs.Name, ms);
        }

        public static void RecordPageLoadLatency(IMetricsManager metrics, double ms)
        {
            if (metrics is null) return;
            TryRegister(metrics, PageLoadLatencyMs);
            metrics.Record(PageLoadLatencyMs.Name, ms);
        }

        public static void RecordComponentRenderLatency(IMetricsManager metrics, double ms)
        {
            if (metrics is null) return;
            TryRegister(metrics, ComponentRenderLatencyMs);
            metrics.Record(ComponentRenderLatencyMs.Name, ms);
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
