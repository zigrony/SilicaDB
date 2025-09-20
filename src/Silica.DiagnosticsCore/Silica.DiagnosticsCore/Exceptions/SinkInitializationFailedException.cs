// Filename: Silica.DiagnosticsCore/Exceptions/SinkInitializationFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    public sealed class SinkInitializationFailedException : SilicaException
    {
        public string SinkType { get; }
        public string Reason { get; }

        public SinkInitializationFailedException(string sinkType, string reason, Exception? inner = null)
            : base(
                code: DiagnosticsCoreExceptions.SinkInitializationFailed.Code,
                message: $"Sink '{sinkType}' failed to initialize. Reason={reason}",
                category: DiagnosticsCoreExceptions.SinkInitializationFailed.Category,
                exceptionId: DiagnosticsCoreExceptions.Ids.SinkInitializationFailed,
                innerException: inner)
        {
            DiagnosticsCoreExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SinkType = sinkType ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }
}
