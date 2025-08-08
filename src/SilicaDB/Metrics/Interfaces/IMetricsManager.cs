using System.Collections.Generic;

namespace SilicaDB.Metrics
{
    public interface IMetricsManager
    {
        void Register(MetricDefinition metricDefinition);

        void Increment(
            string name,
            long value = 1,
            params KeyValuePair<string, object>[] tags);

        void Add(
            string name,
            long delta,
            params KeyValuePair<string, object>[] tags);

        void Record(
            string name,
            double value,
            params KeyValuePair<string, object>[] tags);

        // ③ allow removing individual or all metrics
        void Unregister(string name);
        void ClearAll();
    }
}
