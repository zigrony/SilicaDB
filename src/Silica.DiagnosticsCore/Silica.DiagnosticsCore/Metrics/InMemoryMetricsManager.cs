// File: Silica.DiagnosticsCore/Metrics/InMemoryMetricsManager.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// An in‑memory implementation of <see cref="IMetricsManager"/> suitable for tests
    /// and ephemeral/local runs. Thread‑safe for concurrent register/emit operations.
    /// </summary>
    public sealed class InMemoryMetricsManager : IMetricsManager
    {
        private readonly ConcurrentQueue<MetricRecord> _records = new();
        private readonly ConcurrentDictionary<string, MetricDefinition> _definitions =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Snapshot of all recorded metrics in arrival order. Copy is O(n).
        /// </summary>
        public IReadOnlyCollection<MetricRecord> Records => _records.ToArray();

        public void Register(MetricDefinition definition)
        {
            if (definition is null) throw new ArgumentNullException(nameof(definition));

            // Enforce name uniqueness + schema stability
            _definitions.AddOrUpdate(
                definition.Name,
                definition,
                (_, existing) =>
                {
                    if (!existing.Equals(definition))
                        throw new ArgumentException(
                            $"Metric '{existing.Name}' already registered with a different schema.");
                    return existing;
                });
        }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            Emit(name, value, MetricType.Counter, tags);
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            Emit(name, value, MetricType.Histogram, tags);
        }

        /// <summary>
        /// Generic emit that handles auto‑registration for unregistered metrics in test contexts.
        /// </summary>
        private void Emit(string name, object value, MetricType type, params KeyValuePair<string, object>[] tags)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            if (!_definitions.TryGetValue(name, out var def))
            {
                // Auto‑register for test harness; prod impls should reject
                def = new MetricDefinition(
                    Name: name,
                    Type: type,
                    Description: string.Empty,
                    Unit: string.Empty,
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: null,
                    DoubleCallback: null);
                _definitions[name] = def;
            }

            _records.Enqueue(
                new MetricRecord(def, value, tags ?? Array.Empty<KeyValuePair<string, object>>()));
        }

        public void Clear()
        {
            while (_records.TryDequeue(out _)) { }
            _definitions.Clear();
        }

        public sealed class MetricRecord
        {
            public MetricDefinition Definition { get; }
            public object Value { get; }
            public IReadOnlyCollection<KeyValuePair<string, object>> Tags { get; }

            public MetricRecord(
                MetricDefinition definition,
                object value,
                IEnumerable<KeyValuePair<string, object>> tags)
            {
                Definition = definition ?? throw new ArgumentNullException(nameof(definition));
                Value = value;
                if (tags is null)
                {
                    Tags = Array.Empty<KeyValuePair<string, object>>();
                }
                else
                {
                    // Copy without LINQ
                    var list = new List<KeyValuePair<string, object>>();
                    foreach (var t in tags) list.Add(t);
                    Tags = list.ToArray();
                }
            }
        }
    }
}
