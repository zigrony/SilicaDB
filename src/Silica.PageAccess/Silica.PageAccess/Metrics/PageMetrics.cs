using Silica.DiagnosticsCore.Metrics;
using System;
using System.Collections.Generic;

namespace Silica.PageAccess.Metrics
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Metrics: stable names/types for PageAccess. Low-cardinality, BCL-only.
    // ─────────────────────────────────────────────────────────────────────────────

    public static class PageAccessMetrics
    {
        public static readonly MetricDefinition ReadCount = new(
            Name: "pageaccess.read.count",
            Type: MetricType.Counter,
            Description: "PageAccessor read handle acquisitions",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ReadLatencyMs = new(
            Name: "pageaccess.read.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency to acquire read lease and validate header",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteCount = new(
            Name: "pageaccess.write.count",
            Type: MetricType.Counter,
            Description: "PageAccessor write lease acquisitions",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WriteLatencyMs = new(
            Name: "pageaccess.write.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency to validate header and acquire write lease",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition HeaderWriteCount = new(
            Name: "pageaccess.header_write.count",
            Type: MetricType.Counter,
            Description: "Header-only write operations",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition HeaderWriteLatencyMs = new(
            Name: "pageaccess.header_write.latency_ms",
            Type: MetricType.Histogram,
            Description: "Latency of header-only mutations",
            Unit: "ms",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition ErrorCount = new(
            Name: "pageaccess.error.count",
            Type: MetricType.Counter,
            Description: "Errors in PageAccessor operations",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static readonly MetricDefinition WarnCount = new(
            Name: "pageaccess.warn.count",
            Type: MetricType.Counter,
            Description: "Warnings in PageAccessor operations",
            Unit: "ops",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>());

        public static void RegisterAll(IMetricsManager mm, string componentName)
        {
            if (mm is null) throw new ArgumentNullException(nameof(mm));
            if (string.IsNullOrWhiteSpace(componentName)) componentName = "PageAccess";
            var tag = new KeyValuePair<string, object>(TagKeys.Component, componentName);

            void Reg(in MetricDefinition def)
            {
                var d = def with { DefaultTags = new[] { tag } };
                try { mm.Register(d); } catch { }
            }

            Reg(ReadCount);
            Reg(ReadLatencyMs);
            Reg(WriteCount);
            Reg(WriteLatencyMs);
            Reg(HeaderWriteCount);
            Reg(HeaderWriteLatencyMs);
            Reg(ErrorCount);
            Reg(WarnCount);
        }
    }

}
