using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace SilicaDB.Metrics
{
    public class MetricsManager : IMetricsManager, IDisposable
    {
        private readonly Meter _meter;
        private readonly ConcurrentDictionary<string, object> _instruments = new();

        public MetricsManager(string meterName = "SilicaDB")
        {
            _meter = new Meter(meterName);
        }

        public void Register(MetricDefinition def)
        {
            switch (def.Type)
            {
                case MetricType.Counter:
                    var counter = _meter.CreateCounter<long>(
                        def.Name, def.Unit, def.Description);
                    _instruments[def.Name] = counter;
                    break;

                case MetricType.UpDownCounter:
                    var upDown = _meter.CreateUpDownCounter<long>(
                        def.Name, def.Unit, def.Description);
                    _instruments[def.Name] = upDown;
                    break;

                case MetricType.Histogram:
                    var hist = _meter.CreateHistogram<double>(
                        def.Name, def.Unit, def.Description);
                    _instruments[def.Name] = hist;
                    break;

                case MetricType.Gauge:
                    // Manual-gauge: you can Record() on it
                    var gauge = _meter.CreateObservableGauge<double>(
                        def.Name,
                        () => Array.Empty<Measurement<double>>(),
                        def.Unit,
                        def.Description);
                    _instruments[def.Name] = gauge;
                    break;

                case MetricType.ObservableCounter:
                    if (def.LongObservableCallback == null)
                        throw new InvalidOperationException(
                            "ObservableCounter requires a long-callback.");

                    var obsCounter = _meter.CreateObservableCounter<long>(
                        def.Name,
                        def.LongObservableCallback,
                        def.Unit,
                        def.Description);
                    _instruments[def.Name] = obsCounter;
                    break;

                case MetricType.ObservableUpDownCounter:
                    if (def.LongObservableCallback == null)
                        throw new InvalidOperationException(
                            "ObservableUpDownCounter requires a long-callback.");

                    var obsUpDown = _meter.CreateObservableUpDownCounter<long>(
                        def.Name,
                        def.LongObservableCallback,
                        def.Unit,
                        def.Description);
                    _instruments[def.Name] = obsUpDown;
                    break;

                case MetricType.ObservableGauge:
                    if (def.DoubleObservableCallback == null)
                        throw new InvalidOperationException(
                            "ObservableGauge requires a double-callback.");

                    var obsGauge = _meter.CreateObservableGauge<double>(
                        def.Name,
                        def.DoubleObservableCallback,
                        def.Unit,
                        def.Description);
                    _instruments[def.Name] = obsGauge;
                    break;

                default:
                    throw new NotSupportedException(
                        $"MetricType '{def.Type}' is not supported.");
            }
        }

        public void Increment(
            string name,
            long value = 1,
            params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst)
                && inst is Counter<long> counter)
            {
                counter.Add(value, tags);
                return;
            }

            throw new KeyNotFoundException(
                $"Counter '{name}' is not registered.");
        }

        public void Add(
            string name,
            long delta,
            params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst)
                && inst is UpDownCounter<long> upDown)
            {
                upDown.Add(delta, tags);
                return;
            }

            throw new KeyNotFoundException(
                $"UpDownCounter '{name}' is not registered.");
        }

        public void Record(
            string name,
            double value,
            params KeyValuePair<string, object>[] tags)
        {
            if (_instruments.TryGetValue(name, out var inst)
                && inst is Histogram<double> hist)
            {
                hist.Record(value, tags);
                return;
            }

            throw new KeyNotFoundException(
                $"Histogram '{name}' is not registered.");
        }

        public void Dispose()
        {
            _meter.Dispose();
        }
    }
}
