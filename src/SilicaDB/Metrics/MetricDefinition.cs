using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace SilicaDB.Metrics
{
    public class MetricDefinition
    {
        public string Name { get; }
        public MetricType Type { get; }
        public string Description { get; }
        public string Unit { get; }

        // Callbacks for observable instruments
        public Func<IEnumerable<Measurement<long>>> LongObservableCallback { get; }
        public Func<IEnumerable<Measurement<double>>> DoubleObservableCallback { get; }

        public MetricDefinition(
            string name,
            MetricType type,
            string description = null,
            string unit = null,
            Func<IEnumerable<Measurement<long>>> longCallback = null,
            Func<IEnumerable<Measurement<double>>> doubleCallback = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
            Description = description;
            Unit = unit;
            LongObservableCallback = longCallback;
            DoubleObservableCallback = doubleCallback;
        }
    }
}
