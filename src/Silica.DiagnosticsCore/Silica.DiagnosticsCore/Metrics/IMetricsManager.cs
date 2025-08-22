// File: Silica.DiagnosticsCore/Metrics/IMetricsManager.cs
using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Core interface for registering and emitting metrics in Silica.DiagnosticsCore.
    /// </summary>
    /// <remarks>
    /// Implementations MUST:
    /// <list type="bullet">
    ///   <item>Ignore or explicitly validate duplicate registrations for the same metric name.</item>
    ///   <item>Enforce name, type, and tag schema at runtime to prevent drift.</item>
    ///   <item>Guarantee that emissions for unregistered metrics are either rejected or auto‑registered
    ///         with strict schema validation.</item>
    ///   <item>Preserve low cardinality for all tags; reject high‑cardinality keys unless explicitly whitelisted.</item>
    /// </list>
    /// Lightweight enough for unit tests; powerful enough for production exporters.
    /// </remarks>
    public interface IMetricsManager
    {
        /// <summary>
        /// Registers a metric definition so it can be emitted and/or observed.
        /// </summary>
        /// <param name="definition">
        /// The metric definition to register. Names MUST be unique within a process scope.
        /// </param>
        /// <exception cref="ArgumentNullException">If <paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentException">If the name/type/unit are invalid or conflict with an existing definition.</exception>
        void Register(MetricDefinition definition);

        /// <summary>
        /// Increments a counter metric by the given value.
        /// </summary>
        /// <param name="name">The unique name of the metric to increment.</param>
        /// <param name="value">Amount to increment by. Defaults to 1.</param>
        /// <param name="tags">
        /// Optional tags to associate with this measurement; implementations should enforce tag key/value constraints.
        /// </param>
        /// <exception cref="InvalidOperationException">If the metric is not registered and auto‑registration is disabled.</exception>
        void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags);

        /// <summary>
        /// Records a value for a histogram or observable gauge.
        /// </summary>
        /// <param name="name">The unique name of the metric to record.</param>
        /// <param name="value">The measurement value to record.</param>
        /// <param name="tags">
        /// Optional tags to associate with this measurement; implementations should enforce tag key/value constraints.
        /// </param>
        /// <exception cref="InvalidOperationException">If the metric is not registered and auto‑registration is disabled.</exception>
        void Record(string name, double value, params KeyValuePair<string, object>[] tags);
    }
}
