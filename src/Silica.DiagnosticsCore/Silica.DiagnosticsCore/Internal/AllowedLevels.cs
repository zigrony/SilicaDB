namespace Silica.DiagnosticsCore.Internal
{
    internal static class AllowedLevels
    {
        // Canonical, lowercase
        public static readonly string[] TraceAndLogLevels =
            new[] { "trace", "debug", "info", "warn", "error", "fatal" };
    }
}
