namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// The fundamental kinds of metrics supported by the diagnostics core.
    /// </summary>
    public enum MetricType
    {
        /// <summary>
        /// A monotonically increasing count of occurrences.
        /// </summary>
        Counter,

        /// <summary>
        /// Records the distribution of values for statistical analysis.
        /// </summary>
        Histogram,

        /// <summary>
        /// A value observed on demand via a callback.
        /// Useful for gauges that change over time.
        /// </summary>
        ObservableGauge
    }
}
