// Test.App/Helpers/DummyMetricsManager.cs (paste into Test.App project)
using System.Collections.Generic;
using Silica.DiagnosticsCore.Metrics;

public sealed class DummyMetricsManager : IMetricsManager
{
    public void Register(MetricDefinition def) { }
    public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
    public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
    public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
}
