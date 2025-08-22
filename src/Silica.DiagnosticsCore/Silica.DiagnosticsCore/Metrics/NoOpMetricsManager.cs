using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Zero-allocation, zero-state metrics manager for disabled-metrics mode.
    /// Safe to pass throughout the pipeline; all operations are no-ops.
    /// </summary>
    public sealed class NoOpMetricsManager : IMetricsManager
    {
        public void Register(MetricDefinition definition) { /* no-op */ }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            /* no-op */
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            /* no-op */
        }
    }
}
