// Filename: Silica.Sql.Lexer/Metrics/LexerMetrics.cs

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Sql.Lexer.Metrics
{
    /// <summary>
    /// Canonical, low-cardinality Lexer metric definitions and helpers for DiagnosticsCore.
    /// Contract-first: stable names, types, units, and allowed tags.
    /// No LINQ/reflection/JSON/third-party libs.
    /// </summary>
    public static class LexerMetrics
    {
        // --------------------------
        // CORE LEXING METRICS
        // --------------------------

        public static readonly MetricDefinition TokenCount = new(
            Name: "lexer.token.count",
            Type: MetricType.Counter,
            Description: "Total tokens emitted by the lexer",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ErrorCount = new(
            Name: "lexer.error.count",
            Type: MetricType.Counter,
            Description: "Lexical errors encountered",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition RecoveryCount = new(
            Name: "lexer.recovery.count",
            Type: MetricType.Counter,
            Description: "Error recovery attempts during lexing",
            Unit: "entries",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LexingLatencyMs = new(
            Name: "lexer.latency_ms",
            Type: MetricType.Histogram,
            Description: "Lexing duration per input unit (e.g., per statement or batch)",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition BytesProcessed = new(
            Name: "lexer.bytes.processed",
            Type: MetricType.Histogram,
            Description: "Bytes processed by the lexer",
            Unit: "bytes",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition LinesProcessed = new(
            Name: "lexer.lines.processed",
            Type: MetricType.Histogram,
            Description: "Source lines processed by the lexer",
            Unit: "lines",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        // =====================================================================
        // REGISTRATION
        // =====================================================================

        /// <summary>
        /// Registers all Lexer metric definitions with the metrics manager.
        /// Tag set is low-cardinality and uses TagKeys.Component bound to the lexer component name.
        /// </summary>
        public static void RegisterAll(
            IMetricsManager metrics,
            string lexerComponentName)
        {
            if (metrics is null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrWhiteSpace(lexerComponentName))
                lexerComponentName = "Silica.Sql.Lexer";

            var defTag = new KeyValuePair<string, object>(TagKeys.Component, lexerComponentName);
            var single = new[] { defTag };

            void Reg(MetricDefinition def)
            {
                var d = def with { DefaultTags = single };
                TryRegister(metrics, d);
            }

            // Core metrics
            Reg(TokenCount);
            Reg(ErrorCount);
            Reg(RecoveryCount);
            Reg(LexingLatencyMs);
            Reg(BytesProcessed);
            Reg(LinesProcessed);
        }

        // =====================================================================
        // EVENT EMISSION HELPERS
        // =====================================================================

        public static void IncrementTokenCount(IMetricsManager metrics)
        {
            if (metrics is null) return;
            try { metrics.Increment(TokenCount.Name, 1); } catch { }
        }

        public static void IncrementErrorCount(IMetricsManager metrics)
        {
            if (metrics is null) return;
            try { metrics.Increment(ErrorCount.Name, 1); } catch { }
        }

        public static void IncrementRecoveryCount(IMetricsManager metrics)
        {
            if (metrics is null) return;
            try { metrics.Increment(RecoveryCount.Name, 1); } catch { }
        }

        public static void RecordLexingLatency(IMetricsManager metrics, double latencyMs)
        {
            if (metrics is null) return;
            try { metrics.Record(LexingLatencyMs.Name, latencyMs); } catch { }
        }

        public static void RecordBytesProcessed(IMetricsManager metrics, long bytes)
        {
            if (metrics is null) return;
            if (bytes <= 0) return;
            try { metrics.Record(BytesProcessed.Name, bytes); } catch { }
        }

        public static void RecordLinesProcessed(IMetricsManager metrics, long lines)
        {
            if (metrics is null) return;
            if (lines <= 0) return;
            try { metrics.Record(LinesProcessed.Name, lines); } catch { }
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
