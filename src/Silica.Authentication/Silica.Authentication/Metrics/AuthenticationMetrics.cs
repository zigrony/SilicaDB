using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality Authentication metric definitions and helpers.
    /// Mirrors ConcurrencyMetrics: contract-first, safe, and reusable.
    /// </summary>
    public static class AuthenticationMetrics
    {
        // --------------------------
        // AUTH LIFECYCLE
        // --------------------------
        public static readonly MetricDefinition AttemptCount = new(
            Name: "authentication.attempt.count",
            Type: MetricType.Counter,
            Description: "Authentication attempts (before evaluating outcome)",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SuccessCount = new(
            Name: "authentication.success.count",
            Type: MetricType.Counter,
            Description: "Successful authentications granted",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition FailureCount = new(
            Name: "authentication.failure.count",
            Type: MetricType.Counter,
            Description: "Failed authentications",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LatencyMs = new(
            Name: "authentication.latency.ms",
            Type: MetricType.Histogram,
            Description: "Latency to evaluate authentication attempt",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // POLICY / PROVIDER EVENTS
        // --------------------------
        public static readonly MetricDefinition LockoutCount = new(
            Name: "authentication.lockout.count",
            Type: MetricType.Counter,
            Description: "Accounts locked by policy or repeated failures",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition TokenExpiredCount = new(
            Name: "authentication.token.expired.count",
            Type: MetricType.Counter,
            Description: "Expired tokens encountered",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ProviderUnavailableCount = new(
            Name: "authentication.provider.unavailable.count",
            Type: MetricType.Counter,
            Description: "External authentication provider unavailable",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition UnexpectedErrorCount = new(
            Name: "authentication.unexpected.error.count",
            Type: MetricType.Counter,
            Description: "Unexpected internal errors during authentication",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TAG FIELDS (low cardinality)
        // --------------------------
        public static class Fields
        {
            // Use TagKeys.Field for the method (Local/Jwt/Cert/Kerberos/etc.) to mirror Concurrency’s pattern.
            public const string Method = "method";
            public const string Reason = "reason"; // optional, low-cardinality failure reason (invalid_credentials, account_locked, token_expired, provider_unavailable)
        }

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "Authentication";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(AttemptCount);
            Reg(SuccessCount);
            Reg(FailureCount);
            Reg(LatencyMs);
            Reg(LockoutCount);
            Reg(TokenExpiredCount);
            Reg(ProviderUnavailableCount);
            Reg(UnexpectedErrorCount);
        }

        // --------------------------
        // HELPERS
        // --------------------------
        public static void IncrementAttempt(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, AttemptCount);
            metrics.Increment(AttemptCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementSuccess(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, SuccessCount);
            metrics.Increment(SuccessCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementFailure(IMetricsManager metrics, string method, string reason)
        {
            if (metrics is null) return;
            TryRegister(metrics, FailureCount);
            metrics.Increment(
                FailureCount.Name,
                1,
                new KeyValuePair<string, object>(TagKeys.Field, method),
                new KeyValuePair<string, object>(Fields.Reason, reason));
        }

        public static void RecordLatency(IMetricsManager metrics, double elapsedMs, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, LatencyMs);
            metrics.Record(LatencyMs.Name, elapsedMs, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementLockout(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, LockoutCount);
            metrics.Increment(LockoutCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementTokenExpired(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, TokenExpiredCount);
            metrics.Increment(TokenExpiredCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementProviderUnavailable(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, ProviderUnavailableCount);
            metrics.Increment(ProviderUnavailableCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        public static void IncrementUnexpectedError(IMetricsManager metrics, string method)
        {
            if (metrics is null) return;
            TryRegister(metrics, UnexpectedErrorCount);
            metrics.Increment(UnexpectedErrorCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, method));
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
