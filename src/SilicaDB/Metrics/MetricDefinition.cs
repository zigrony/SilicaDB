using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;              // ① for Select() / ToArray()

namespace SilicaDB.Metrics
{
    /// <summary>
    /// Describes a metric: its name, type, unit, description, and default tags.
    /// Callbacks (for observables) are only mutable during bootstrap.
    /// </summary>
    public class MetricDefinition
    {
        public string Name { get; }
        public MetricType Type { get; }
        public string Description { get; }
        public string Unit { get; }
        public KeyValuePair<string, object>[] DefaultTags { get; }

        // Callbacks for observable instruments; internal set only for initial wiring.
        public Func<IEnumerable<Measurement<long>>> LongObservableCallback { get; internal set; }
        public Func<IEnumerable<Measurement<double>>> DoubleObservableCallback { get; internal set; }

        public MetricDefinition(
            string name,
            MetricType type,
            string description = null,
            string unit = null,
            (string Key, object Value)[] defaultTags = null,
            Func<IEnumerable<Measurement<long>>> longCallback = null,
            Func<IEnumerable<Measurement<double>>> doubleCallback = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
            Description = description;
            Unit = unit;
            LongObservableCallback = longCallback;
            DoubleObservableCallback = doubleCallback;

            // Capture default tags or empty
            DefaultTags = defaultTags?
                .Select(t => new KeyValuePair<string, object>(t.Key, t.Value))
                .ToArray()
                ?? Array.Empty<KeyValuePair<string, object>>();
        }

        /// <summary>
        /// Replace any "{deviceName}" placeholder in the "device" tag.
        /// </summary>
        internal void ResolveDeviceTag(string deviceName)
        {
            for (int i = 0; i < DefaultTags.Length; i++)
            {
                if (DefaultTags[i].Key == "device"
                    && DefaultTags[i].Value is string s
                    && s == "{deviceName}")
                {
                    DefaultTags[i] = new KeyValuePair<string, object>("device", deviceName);
                }
            }
        }
    }
}
