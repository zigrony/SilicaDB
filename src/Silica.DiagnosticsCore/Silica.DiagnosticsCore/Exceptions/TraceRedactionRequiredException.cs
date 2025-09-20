// Filename: Silica.DiagnosticsCore/Exceptions/TraceRedactionRequiredException.cs
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    public sealed class TraceRedactionRequiredException : SilicaException
    {
        public string Component { get; }
        public string Operation { get; }

        public TraceRedactionRequiredException(string component, string operation)
            : base(
                code: DiagnosticsCoreExceptions.TraceRedactionRequired.Code,
                message: $"Trace redaction required for {component}/{operation} but event was not sanitized.",
                category: DiagnosticsCoreExceptions.TraceRedactionRequired.Category,
                exceptionId: DiagnosticsCoreExceptions.Ids.TraceRedactionRequired)
        {
            DiagnosticsCoreExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Component = component ?? string.Empty;
            Operation = operation ?? string.Empty;
        }
    }
}
