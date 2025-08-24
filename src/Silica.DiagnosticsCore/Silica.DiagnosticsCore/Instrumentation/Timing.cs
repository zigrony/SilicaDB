// File: Silica.DiagnosticsCore/Instrumentation/Timing.cs
namespace Silica.DiagnosticsCore.Instrumentation
{
    public static class Timing
    {
        public static TimingScope Start(string component, string operation, Action<TimingScope> onComplete)
        {
            // Requires DiagnosticsCoreBootstrap.Start(...) to have been called by the host.
            // High-resolution timing is always used; the obsolete option is ignored.
            var _ = DiagnosticsCoreBootstrap.Instance;
            var useHighRes = true;
            // Or, if you really want to honor the (obsolete) option for now:
            // var useHighRes = DiagnosticsCoreBootstrap.Instance.Options.UseHighResolutionTiming;

            return new TimingScope(component, operation, onComplete, useHighRes);
        }
    }
}
