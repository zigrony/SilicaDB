using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Sessions.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality Sessions metric definitions and helpers.
    /// Contract-first, stable names, and reusable across hosts.
    /// </summary>
    public static class SessionMetrics
    {
        // --------------------------
        // SESSION LIFECYCLE
        // --------------------------
        public static readonly MetricDefinition SessionCreateCount = new(
            Name: "sessions.session.create.count",
            Type: MetricType.Counter,
            Description: "Sessions created",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionCloseCount = new(
            Name: "sessions.session.close.count",
            Type: MetricType.Counter,
            Description: "Sessions closed",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionExpireCount = new(
            Name: "sessions.session.expire.count",
            Type: MetricType.Counter,
            Description: "Sessions expired",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionEvictCount = new(
            Name: "sessions.session.evict.count",
            Type: MetricType.Counter,
            Description: "Idle sessions evicted by sweep",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionTouchCount = new(
            Name: "sessions.session.touch.count",
            Type: MetricType.Counter,
            Description: "Session activity touches",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionIdleSetCount = new(
            Name: "sessions.session.idle.set.count",
            Type: MetricType.Counter,
            Description: "Sessions marked idle",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionActiveGauge = new(
            Name: "sessions.session.active.gauge",
            Type: MetricType.ObservableGauge,
            Description: "Approximate active sessions (per component)",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionStateTransitionCount = new(
            Name: "sessions.session.state.transition.count",
            Type: MetricType.Counter,
            Description: "Session state transitions",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionQuerySetCount = new(
            Name: "sessions.session.query.set.count",
            Type: MetricType.Counter,
            Description: "CurrentQuery set on session",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionTagSetCount = new(
            Name: "sessions.session.tag.set.count",
            Type: MetricType.Counter,
            Description: "Diagnostic tags set on session",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition SessionTagRemoveCount = new(
            Name: "sessions.session.tag.remove.count",
            Type: MetricType.Counter,
            Description: "Diagnostic tags removed from session",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TRANSACTION ANCHOR
        // --------------------------
        public static readonly MetricDefinition TxBeginCount = new(
            Name: "sessions.tx.begin.count",
            Type: MetricType.Counter,
            Description: "Transactions begun in sessions",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition TxEndCount = new(
            Name: "sessions.tx.end.count",
            Type: MetricType.Counter,
            Description: "Transactions ended in sessions",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition TxLatencyMs = new(
            Name: "sessions.tx.latency_ms",
            Type: MetricType.Histogram,
            Description: "Transaction duration recorded at end",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TIMEOUT ENFORCEMENT
        // --------------------------
        public static readonly MetricDefinition TxTimeoutAbortCount = new(
            Name: "sessions.tx.timeout.abort.count",
            Type: MetricType.Counter,
            Description: "Transactions aborted due to timeout policy",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // --------------------------
        // TAG FIELDS (low cardinality)
        // --------------------------
        public static class Fields
        {
            public const string State = "state";            // Created, Authenticated, Active, Idle, Closed, Expired
            public const string TxState = "tx_state";       // Active, Committed, RolledBack, Aborted, None
            public const string PrincipalPresent = "principal_present"; // yes/no
        }

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll(
            IMetricsManager metrics,
            string componentName,
            Func<long>? activeSessionsProvider = null)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "Silica.Sessions";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, componentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            Reg(SessionCreateCount);
            Reg(SessionCloseCount);
            Reg(SessionExpireCount);
            Reg(SessionEvictCount);
            Reg(SessionTouchCount);
            Reg(SessionIdleSetCount);
            Reg(SessionStateTransitionCount);
            Reg(SessionQuerySetCount);
            Reg(SessionTagSetCount);
            Reg(SessionTagRemoveCount);

            Reg(TxBeginCount);
            Reg(TxEndCount);
            Reg(TxLatencyMs);
            Reg(TxTimeoutAbortCount);

            if (activeSessionsProvider is not null)
            {
                var g = SessionActiveGauge with
                {
                    LongCallback = () =>
                    {
                        var v = activeSessionsProvider();
                        return new[] { new Measurement<long>(v, defTag) };
                    }
                };
                TryRegister(metrics, g);
            }
        }

        // --------------------------
        // HELPERS
        // --------------------------
        public static void IncrementCreate(IMetricsManager m, string principalPresent)
        {
            if (m is null) return;
            TryRegister(m, SessionCreateCount);
            m.Increment(SessionCreateCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, principalPresent));
        }

        public static void IncrementClose(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionCloseCount);
            m.Increment(SessionCloseCount.Name, 1);
        }

        public static void IncrementExpire(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionExpireCount);
            m.Increment(SessionExpireCount.Name, 1);
        }

        public static void IncrementEvict(IMetricsManager m, int count)
        {
            if (m is null) return;
            if (count <= 0) return;
            TryRegister(m, SessionEvictCount);
            m.Increment(SessionEvictCount.Name, count);
        }

        public static void IncrementTouch(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionTouchCount);
            m.Increment(SessionTouchCount.Name, 1);
        }

        public static void IncrementIdleSet(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionIdleSetCount);
            m.Increment(SessionIdleSetCount.Name, 1);
        }

        public static void IncrementStateTransition(IMetricsManager m, string toState)
        {
            if (m is null) return;
            TryRegister(m, SessionStateTransitionCount);
            m.Increment(SessionStateTransitionCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, toState));
        }

        public static void IncrementQuerySet(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionQuerySetCount);
            m.Increment(SessionQuerySetCount.Name, 1);
        }

        public static void IncrementTagSet(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, SessionTagSetCount);
            m.Increment(SessionTagSetCount.Name, 1);
        }

        public static void IncrementTagRemove(IMetricsManager m, int count)
        {
            if (m is null) return;
            if (count <= 0) return;
            TryRegister(m, SessionTagRemoveCount);
            m.Increment(SessionTagRemoveCount.Name, count);
        }

        public static void IncrementTxBegin(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, TxBeginCount);
            m.Increment(TxBeginCount.Name, 1);
        }

        public static void IncrementTxEnd(IMetricsManager m, string finalState)
        {
            if (m is null) return;
            TryRegister(m, TxEndCount);
            m.Increment(TxEndCount.Name, 1, new KeyValuePair<string, object>(TagKeys.Field, finalState));
        }

        public static void RecordTxLatency(IMetricsManager m, double ms)
        {
            if (m is null) return;
            TryRegister(m, TxLatencyMs);
            m.Record(TxLatencyMs.Name, ms);
        }

        public static void IncrementTxTimeoutAbort(IMetricsManager m)
        {
            if (m is null) return;
            TryRegister(m, TxTimeoutAbortCount);
            m.Increment(TxTimeoutAbortCount.Name, 1);
        }

        private static void TryRegister(IMetricsManager metrics, MetricDefinition def)
        {
            try { metrics.Register(def); } catch { /* ignore duplicate */ }
        }
    }
}
