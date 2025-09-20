// Filename: Silica.DiagnosticsCore/Exceptions/DispatcherFullModeUnsupportedException.cs
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    public sealed class DispatcherFullModeUnsupportedException : SilicaException
    {
        public string RequestedMode { get; }
        public string EffectiveMode { get; }
        public bool StrictBootstrap { get; }

        public DispatcherFullModeUnsupportedException(string requestedMode, string effectiveMode, bool strictBootstrap)
            : base(
                code: DiagnosticsCoreExceptions.DispatcherFullModeUnsupported.Code,
                message: $"DispatcherFullMode='{requestedMode}' is unsupported." +
                         (strictBootstrap
                             ? " StrictBootstrapOptions=true prevents coercion."
                             : $" Coerced to '{effectiveMode}'."),
                category: DiagnosticsCoreExceptions.DispatcherFullModeUnsupported.Category,
                exceptionId: DiagnosticsCoreExceptions.Ids.DispatcherFullModeUnsupported)
        {
            DiagnosticsCoreExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            RequestedMode = requestedMode ?? string.Empty;
            EffectiveMode = effectiveMode ?? string.Empty;
            StrictBootstrap = strictBootstrap;
        }
    }
}
