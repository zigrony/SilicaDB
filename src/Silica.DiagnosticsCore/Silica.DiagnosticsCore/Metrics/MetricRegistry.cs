// File: Silica.DiagnosticsCore/Metrics/MetricRegistry.cs
using System;
using System.Collections.Concurrent;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Thread‑safe registry of known metric definitions.
    /// </summary>
    /// <remarks>
    /// Guarantees:
    /// <list type="bullet">
    ///   <item>Metric names are unique (case‑insensitive) within the process.</item>
    ///   <item>Schema (type/unit/default tags) is immutable after registration.</item>
    ///   <item>All definitions are validated at registration time.</item>
    /// </list>
    /// </remarks>
    public sealed class MetricRegistry
    {
        private readonly ConcurrentDictionary<string, MetricDefinition> _definitions =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers a metric definition after validating its schema.
        /// </summary>
        /// <param name="definition">The metric definition to register.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// If the name already exists with a different schema.
        /// </exception>
        public void Register(MetricDefinition definition)
        {
            if (definition is null)
                throw new ArgumentNullException(nameof(definition));

            definition.EnsureValid();

            _definitions.AddOrUpdate(
                definition.Name,
                definition,
                (_, existing) =>
                {
                    if (!existing.Equals(definition))
                        throw new ArgumentException(
                            $"Metric '{existing.Name}' is already registered with a different schema.");
                    return existing;
                });
        }

        /// <summary>
        /// Returns true if a definition for the given metric name exists.
        /// </summary>
        /// <param name="name">The metric name to check.</param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="name"/> is null, empty, or whitespace.
        /// </exception>
        public bool IsKnown(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    "Metric name must not be null or whitespace.", nameof(name));

            return _definitions.ContainsKey(name);
        }

        /// <summary>
        /// Retrieves the full definition for a registered metric.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <returns>The registered <see cref="MetricDefinition"/>.</returns>
        /// <exception cref="KeyNotFoundException">If the metric is not registered.</exception>
        public MetricDefinition GetDefinition(string name)
        {
            if (!_definitions.TryGetValue(name, out var def))
                throw new KeyNotFoundException($"Metric '{name}' is not registered.");
            return def;
        }
    }
}
