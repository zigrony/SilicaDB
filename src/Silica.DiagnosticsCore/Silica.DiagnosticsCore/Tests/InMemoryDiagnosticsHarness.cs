// File: Silica.DiagnosticsCore.Tests/InMemoryDiagnosticsHarness.cs
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;

namespace Silica.DiagnosticsCore.Tests
{
    /// <summary>
    /// Aggregates in-memory diagnostics capture for both metrics and traces.
    /// Primarily intended for integration and unit testing.
    /// Thread-safe for independent Metrics/Traces access.
    /// </summary>
    public sealed class InMemoryDiagnosticsHarness
    {
        /// <summary>
        /// Captures all metrics emitted during the test run.
        /// </summary>
        public InMemoryMetricsManager Metrics { get; }

        /// <summary>
        /// Captures all traces emitted during the test run.
        /// </summary>
        public InMemoryTraceSink Traces { get; }

        /// <summary>
        /// Initializes a new in-memory diagnostics harness with fresh metrics and trace sinks.
        /// </summary>
        public InMemoryDiagnosticsHarness()
        {
            Metrics = new InMemoryMetricsManager();
            Traces = new InMemoryTraceSink();
        }

        /// <summary>
        /// Resets all captured diagnostics data.
        /// Safe to call between tests to reuse the same harness instance.
        /// </summary>
        public void Clear()
        {
            Metrics?.Clear();
            Traces?.Clear();
        }
    }
}
