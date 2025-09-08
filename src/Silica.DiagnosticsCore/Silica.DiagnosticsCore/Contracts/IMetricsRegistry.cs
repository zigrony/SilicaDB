using System;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Describes a single metric, including its name, description, unit, and optional tags.
    /// </summary>
    public sealed record MetricDescriptor(
        string Name,
        string Description,
        string Unit,
        string[] Tags
    );

    /// <summary>
    /// Contract for registering metrics at runtime.
    /// Implementations handle storage, publishing, and integration with the metrics backend.
    /// </summary>
    public interface IMetricsRegistry
    {
        /// <summary>
        /// Registers a metric definition with the diagnostics system.
        /// </summary>
        /// <param name="descriptor">The metric descriptor to register.</param>
        void Register(MetricDescriptor descriptor);
    }
}
