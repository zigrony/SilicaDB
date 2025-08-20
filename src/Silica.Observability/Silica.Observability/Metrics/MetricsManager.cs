using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Collections.Generic;
using Silica.Observability.Metrics.Interfaces;
using Silica.Observability.Tracing;
using Silica.Observability.Tracing.Sinks;
namespace Silica.Observability.Metrics
{
    public class MetricsManager : IMetricsManager, IDisposable
    {
        private readonly Meter _meter;
        private readonly ConcurrentDictionary<string, object> _instruments = new();
        private readonly ConcurrentDictionary<string, MetricDefinition> _definitions = new();

        public MetricsManager(string meterName = "SilicaDB")
        {
            _meter = new Meter(meterName);
        }

        public void Register(MetricDefinition def)
        {
            // ④ throw on duplicate registration
            if (!_definitions.TryAdd(def.Name, def))
                throw new InvalidOperationException($"Metric '{def.Name}' is already registered.");

            try
            {
                object instrument = def.Type switch
                {
                    MetricType.Counter =>
                        _meter.CreateCounter<long>(def.Name, def.Unit, def.Description),

                    MetricType.UpDownCounter =>
                        _meter.CreateUpDownCounter<long>(def.Name, def.Unit, def.Description),

                    MetricType.Histogram =>
                        _meter.CreateHistogram<double>(def.Name, def.Unit, def.Description),

                    MetricType.Gauge =>
                        _meter.CreateObservableGauge<double>(
                            def.Name,
                            () => Array.Empty<Measurement<double>>(),
                            def.Unit,
                            def.Description),

                    MetricType.ObservableCounter when def.LongObservableCallback != null =>
                        _meter.CreateObservableCounter<long>(
                            def.Name,
                            def.LongObservableCallback,
                            def.Unit,
                            def.Description),

                    MetricType.ObservableUpDownCounter when def.LongObservableCallback != null =>
                        _meter.CreateObservableUpDownCounter<long>(
                            def.Name,
                            def.LongObservableCallback,
                            def.Unit,
                            def.Description),

                    MetricType.ObservableGauge when def.DoubleObservableCallback != null =>
                        _meter.CreateObservableGauge<double>(
                            def.Name,
                            def.DoubleObservableCallback,
                            def.Unit,
                            def.Description),

                    MetricType.ObservableCounter =>
                        throw new InvalidOperationException("ObservableCounter requires a long-callback."),

                    MetricType.ObservableUpDownCounter =>
                        throw new InvalidOperationException("ObservableUpDownCounter requires a long-callback."),

                    MetricType.ObservableGauge =>
                        throw new InvalidOperationException("ObservableGauge requires a double-callback."),

                    _ => throw new NotSupportedException($"MetricType '{def.Type}' is not supported.")
                };

                _instruments[def.Name] = instrument;
            }
            catch
            {
                // rollback on failure
                _definitions.TryRemove(def.Name, out _);
                throw;
            }
        }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst) && inst is Counter<long> counter)
            {
                counter.Add(value, MergeTags(name, tags));
                return;
            }
            throw new KeyNotFoundException($"Counter '{name}' is not registered.");
        }

        public void Add(string name, long delta, params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst) && inst is UpDownCounter<long> upDown)
            {
                upDown.Add(delta, MergeTags(name, tags));
                return;
            }
            throw new KeyNotFoundException($"UpDownCounter '{name}' is not registered.");
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst) && inst is Histogram<double> hist)
            {
                hist.Record(value, MergeTags(name, tags));
                return;
            }
            throw new KeyNotFoundException($"Histogram '{name}' is not registered.");
        }

        /// <summary>
        /// Remove a single metric and its definition.
        /// </summary>
        public void Unregister(string name)
        {
            if (!_definitions.TryRemove(name, out _))
                throw new KeyNotFoundException($"Metric '{name}' is not registered.");

            _instruments.TryRemove(name, out _);
        }

        /// <summary>
        /// Clear all metrics and definitions.
        /// </summary>
        public void ClearAll()
        {
            _definitions.Clear();
            _instruments.Clear();
        }

        public IReadOnlyList<(string Name, object Value)> Snapshot()
        {
            var results = new List<(string, object)>();

            foreach (var (name, def) in _definitions)
            {
                if (_instruments.TryGetValue(name, out var inst))
                {
                    object value = inst switch
                    {
                        Counter<long> c => "Counter: (value hidden)",
                        UpDownCounter<long> u => "UpDownCounter: (value hidden)",
                        Histogram<double> h => "Histogram: (value hidden)",
                        _ => "(unprintable or unsupported)"
                    };

                    results.Add((name, value));
                }
            }

            return results;
        }

        /// <summary>
        /// Dispose of all instruments by clearing and then disposing the meter.
        /// </summary>
        public void Dispose()
        {
            ClearAll();
            _meter.Dispose();
        }

        private KeyValuePair<string, object>[] MergeTags(
            string metricName,
            KeyValuePair<string, object>[] callTags)
        {
            if (_definitions.TryGetValue(metricName, out var def))
            {
                var merged = new KeyValuePair<string, object>[def.DefaultTags.Length + callTags.Length];
                def.DefaultTags.CopyTo(merged, 0);
                callTags.CopyTo(merged, def.DefaultTags.Length);
                return merged;
            }
            return callTags;
        }
    }
}
