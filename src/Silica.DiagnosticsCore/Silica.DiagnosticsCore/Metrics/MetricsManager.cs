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
        private readonly TagValidator? _metricTagValidator;
        private volatile bool _disposed;

        public MetricsManager(
            MetricRegistry? registry = null,
            string meterName = "Silica.DiagnosticsCore",
            TagValidator? metricTagValidator = null)
        {
            _registry = registry ?? new MetricRegistry();
            _meter = new Meter(meterName ?? throw new ArgumentNullException(nameof(meterName)));
            _metricTagValidator = metricTagValidator;
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

            if (string.IsNullOrWhiteSpace(name))
            {
                // Invalid name: account as invalid_operation for operators; avoid registry exceptions.
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.InvalidOperation);
                return;
            }

            if (value < 0)
            {
                // Negative counter increments are invalid
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.InvalidValue,
                    new KeyValuePair<string, object>(TagKeys.Metric, name));
                return;
            }

            if (!_registry.IsKnown(name))
            {
                // Unknown metric: explicit accounting instead of silent no-op
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.UnknownMetricNoAutoreg,
                    new KeyValuePair<string, object>(TagKeys.Metric, name));
                return;
            }
            if (_counters.TryGetValue(name, out var counter))
            {
                var effectiveTags = (_metricTagValidator is null || tags is null || tags.Length == 0)
                    ? tags
                    : _metricTagValidator.Validate(tags);
                counter.Add(value, effectiveTags);
                return;
            }

            // Known metric but not a counter instrument => type mismatch
            AccountMetricDrop(
                DiagCoreMetrics.MetricsDropped.Name,
                DropCauses.TypeMismatch,
                new KeyValuePair<string, object>(TagKeys.Metric, name));

        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(name))
            {
                // Invalid name: account as invalid_operation; avoid registry exceptions.
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.InvalidOperation);
                return;
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.InvalidValue,
                    new KeyValuePair<string, object>(TagKeys.Metric, name));
                return;
            }

            if (!_registry.IsKnown(name))
            {
                // Unknown metric: explicit accounting instead of silent no-op
                AccountMetricDrop(
                    DiagCoreMetrics.MetricsDropped.Name,
                    DropCauses.UnknownMetricNoAutoreg,
                    new KeyValuePair<string, object>(TagKeys.Metric, name));
                return;
            }
            if (_histograms.TryGetValue(name, out var hist))
            {
                var effectiveTags = (_metricTagValidator is null || tags is null || tags.Length == 0)
                    ? tags
                    : _metricTagValidator.Validate(tags);
                hist.Record(value, effectiveTags);
                return;
            }

            // Known metric but not a histogram instrument => type mismatch
            AccountMetricDrop(
                DiagCoreMetrics.MetricsDropped.Name,
                DropCauses.TypeMismatch,
                new KeyValuePair<string, object>(TagKeys.Metric, name));
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
            // MetricRegistry has no Clear(); retained definitions intentionally persist for process lifetime.
            // If you need to drop definitions, create a new MetricsManager instance with a fresh registry.
        }
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MetricsManager),
                    "MetricsManager has been disposed and cannot be reused.");
        }
        // Ensure drop accounting works even if MetricsDropped wasn't registered yet.
        // Avoid recursive Increment(path) by operating on instruments directly after registration.
        private void AccountMetricDrop(
            string dropMetricName,
            string cause,
            params KeyValuePair<string, object>[] extraTags)
        {
            try
            {
                // Register drop metric if needed
                if (!_registry.IsKnown(dropMetricName))
                {
                    Register(DiagCoreMetrics.MetricsDropped);
                }
                // Retrieve or create the counter instrument
                if (!_counters.TryGetValue(dropMetricName, out var dropCounter))
                {
                    // Register would have created it; GetOrAdd as a safety net
                    dropCounter = _meter.CreateCounter<long>(dropMetricName, DiagCoreMetrics.MetricsDropped.Unit, DiagCoreMetrics.MetricsDropped.Description);
                    _counters.TryAdd(dropMetricName, dropCounter);
                }
                // Build tag list: drop_cause + optional context
                if (extraTags is { Length: > 0 })
                {
                    var all = new KeyValuePair<string, object>[extraTags.Length + 1];
                    all[0] = new KeyValuePair<string, object>(TagKeys.DropCause, cause);
                    Array.Copy(extraTags, 0, all, 1, extraTags.Length);
                    dropCounter.Add(1, all);
                }
                else
                {
                    dropCounter.Add(1, new KeyValuePair<string, object>(TagKeys.DropCause, cause));
                }
            }
            catch
            {
                // Swallow exporter/runtime errors; do not throw on the hot path.
            }
        }

    }
}
