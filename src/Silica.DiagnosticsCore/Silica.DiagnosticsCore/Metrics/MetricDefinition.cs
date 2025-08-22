// File: Silica.DiagnosticsCore/Metrics/MetricDefinition.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Describes a single metric, including its metadata, type, unit,
    /// optional default tags, and optional callbacks for observable metrics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an immutable record type. All arguments are validated at construction.
    /// </para>
    /// <para>
    /// For Observable metrics, exactly one callback must be provided and match the metric type.
    /// </para>
    /// <para>
    /// DefaultTags MUST be low‑cardinality; prefer fixed enumerations for keys/values.
    /// </para>
    /// </remarks>
    public sealed record MetricDefinition(
        string Name,
        MetricType Type,
        string Description,
        string Unit,
        IReadOnlyList<KeyValuePair<string, object>> DefaultTags,
        Func<IEnumerable<Measurement<long>>>? LongCallback = null,
        Func<IEnumerable<Measurement<double>>>? DoubleCallback = null)
    {
        /// <summary>
        /// Throws if required fields are invalid or callbacks don't match the type.
        /// Call once at registration to ensure the definition is safe to use in prod.
        /// </summary>
        public MetricDefinition EnsureValid()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException("Metric name must be provided.", nameof(Name));

            if (!Enum.IsDefined(typeof(MetricType), Type))
                throw new ArgumentOutOfRangeException(nameof(Type), Type, "Unknown MetricType value.");


            // Enforce conservative name schema to avoid downstream exporter rejects
            for (int i = 0; i < Name.Length; i++)
            {
                char c = Name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
                if (!ok)
                    throw new ArgumentException(
                        $"Metric name '{Name}' contains invalid char '{c}'. Allowed: [a-z0-9._]");
            }

            if (Type is MetricType.ObservableGauge)
            {
                if (LongCallback is null && DoubleCallback is null)
                    throw new ArgumentException("Observable metrics require at least one callback.");
                if (LongCallback is not null && DoubleCallback is not null)
                    throw new ArgumentException("Only one callback should be provided for an observable metric.");
            }
            else
            {
                if (LongCallback is not null || DoubleCallback is not null)
                    throw new ArgumentException("Callbacks are only valid for observable metrics.");
            }

            if (DefaultTags is null)
                throw new ArgumentNullException(nameof(DefaultTags), "DefaultTags must not be null (use empty array if none).");

            return this;
        }
    }
}
