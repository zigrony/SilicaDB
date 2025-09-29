using System;
using System.Collections.Generic;
using System.Threading;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// In-memory bounded sink for metrics, mirroring the trace sink pattern.
    /// Keeps a rolling buffer of the most recent metric updates for dashboard/API use.
    /// </summary>
    public sealed class BoundedInMemoryMetricsSink
    {
        private readonly MetricRecord[] _buffer;
        private int _nextIndex;
        private int _count;
        private readonly object _lock = new object();

        public int Capacity { get; }

        public BoundedInMemoryMetricsSink(int capacity = 1024)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            _buffer = new MetricRecord[capacity];
            _nextIndex = 0;
            _count = 0;
        }

        /// <summary>
        /// Record a metric update into the buffer.
        /// </summary>
        public void Record(string name, string type, double value, KeyValuePair<string, object>[] tags)
        {
            if (string.IsNullOrEmpty(name)) return;

            var rec = new MetricRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Name = name,
                Type = type ?? string.Empty,
                Value = value,
                Tags = tags ?? Array.Empty<KeyValuePair<string, object>>()
            };

            lock (_lock)
            {
                _buffer[_nextIndex] = rec;
                _nextIndex = (_nextIndex + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>
        /// Return a snapshot of the current buffer contents in chronological order.
        /// </summary>
        public IReadOnlyList<MetricRecord> GetSnapshot()
        {
            var result = new List<MetricRecord>(_count);
            lock (_lock)
            {
                int start = (_nextIndex - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (start + i) % Capacity;
                    result.Add(_buffer[idx]);
                }
            }
            return result;
        }

        /// <summary>
        /// Simple record type for metric updates.
        /// </summary>
        public readonly struct MetricRecord
        {
            public DateTimeOffset Timestamp { get; init; }
            public string Name { get; init; }
            public string Type { get; init; }
            public double Value { get; init; }
            public IReadOnlyList<KeyValuePair<string, object>> Tags { get; init; }
        }
    }
}
