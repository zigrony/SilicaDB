// Filename: Silica.DiagnosticsCore/Exceptions/MetricsRegistrationConflictException.cs
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    public sealed class MetricsRegistrationConflictException : SilicaException
    {
        public string MetricName { get; }

        public MetricsRegistrationConflictException(string metricName)
            : base(
                code: DiagnosticsCoreExceptions.MetricsRegistrationConflict.Code,
                message: $"Metric '{metricName}' registration conflicted with an existing schema.",
                category: DiagnosticsCoreExceptions.MetricsRegistrationConflict.Category,
                exceptionId: DiagnosticsCoreExceptions.Ids.MetricsRegistrationConflict)
        {
            DiagnosticsCoreExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            MetricName = metricName ?? string.Empty;
        }
    }
}
