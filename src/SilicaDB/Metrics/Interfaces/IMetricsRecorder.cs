// File: IMetricsRecorder.cs
using System;
using System.Threading.Tasks;

namespace SilicaDB.Metrics.Interfaces
{
    /// <summary>
    /// Abstraction for recording application metrics.
    /// All methods enqueue work and return immediately.
    /// </summary>
    public interface IMetricsRecorder : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Increment a named counter by the specified delta (default = 1).
        /// </summary>
        ValueTask IncrementCounterAsync(string name, long delta = 1);

        /// <summary>
        /// Record a value into a named histogram.
        /// </summary>
        ValueTask RecordHistogramAsync(string name, double value);

        /// <summary>
        /// Record an instantaneous gauge value.
        /// </summary>
        ValueTask RecordGaugeAsync(string name, double value);

        /// <summary>
        /// Flushes all pending metric events to the backend without disposing.
        /// Returns when the queue is fully drained.
        /// </summary>
        ValueTask FlushAsync();
    }
}
