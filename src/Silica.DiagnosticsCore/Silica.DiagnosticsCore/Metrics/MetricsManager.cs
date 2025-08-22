// File: Silica.DiagnosticsCore/Metrics/MetricsManager.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Thread‑safe, in‑process metrics registry and emitter.
    /// Designed to bridge to any <see cref="MeterListener"/> or exporter without code changes.
    /// </summary>
    public sealed class MetricsManager : IMetricsManager, IDisposable
    {
        private readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
        private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
        private readonly MetricRegistry _registry;
        private readonly Meter _meter;
        private volatile bool _disposed;

        public MetricsManager(MetricRegistry? registry = null, string meterName = "Silica.DiagnosticsCore")
        {
            _registry = registry ?? new MetricRegistry();
            _meter = new Meter(meterName ?? throw new ArgumentNullException(nameof(meterName)));
        }

        public void Register(MetricDefinition definition)
        {
            ThrowIfDisposed();
            if (definition is null) throw new ArgumentNullException(nameof(definition));

            definition.EnsureValid();

            // First, register in registry (will throw if conflicting schema)
            _registry.Register(definition);

            switch (definition.Type)
            {
                case MetricType.Counter:
                    _counters.GetOrAdd(
                        definition.Name,
                        _ => _meter.CreateCounter<long>(
                            definition.Name,
                            definition.Unit,
                            definition.Description));
                    break;

                case MetricType.Histogram:
                    _histograms.GetOrAdd(
                        definition.Name,
                        _ => _meter.CreateHistogram<double>(
                            definition.Name,
                            definition.Unit,
                            definition.Description));
                    break;

                case MetricType.ObservableGauge:
                    // Observable gauges are safe as-is; Meter ignores duplicate creation
                    if (definition.LongCallback is not null)
                        _meter.CreateObservableGauge(definition.Name, definition.LongCallback, definition.Unit, definition.Description);
                    else if (definition.DoubleCallback is not null)
                        _meter.CreateObservableGauge(definition.Name, definition.DoubleCallback, definition.Unit, definition.Description);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(definition.Type), definition.Type, "Unsupported metric type.");
            }
        }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            ThrowIfDisposed();
            if (!_registry.IsKnown(name)) return;
            if (_counters.TryGetValue(name, out var counter))
                counter.Add(value, tags);
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            ThrowIfDisposed();
            if (!_registry.IsKnown(name)) return;
            if (_histograms.TryGetValue(name, out var hist))
                hist.Record(value, tags);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; // fail-fast future calls
            Clear();
            _meter.Dispose();
        }

        private void Clear()
        {
            _counters.Clear();
            _histograms.Clear();
            // MetricRegistry has no Clear(), so either replace it or leave as-is
            // If you want to drop definitions too, reassign a new registry:
            // _registry = new MetricRegistry();
        }
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MetricsManager),
                    "MetricsManager has been disposed and cannot be reused.");
        }
    }
}
