using SilicaDB.Diagnostics.Tracing;
using SilicaDB.Diagnostics.Tracing.Sinks;
using SilicaDB.Metrics;

namespace SilicaDB.Bootstrap;

public static class DiagnosticsBootstrap
{
    private static IDisposable? _consoleReg, _bufferReg, _fileReg, _relayReg;

    public static void Configure(IMetricsManager metrics, bool devMode, string? traceDir = null)
    {
        Trace.Enabled = true;
        Trace.EnableCategories(Enum.GetValues<TraceCategory>()); // all on; refine per env

        // Common buffer (useful in tests and support bundles)
        _bufferReg = Trace.RegisterSink(new BoundedTraceBuffer(8192));

        if (devMode)
            _consoleReg = Trace.RegisterSink(new ConsoleTraceSink());

        if (!string.IsNullOrEmpty(traceDir))
            _fileReg = Trace.RegisterSink(new FileTraceSink(traceDir, prefix: "silica"));

        _relayReg = Trace.RegisterSink(new MetricsRelaySink(metrics));
    }

    public static async Task ShutdownAsync()
    {
        // Unregister triggers Dispose on sinks
        _consoleReg?.Dispose();
        _bufferReg?.Dispose();
        _fileReg?.Dispose();
        _relayReg?.Dispose();

        // Nothing else to flush; file sink disposes writer
        await Task.CompletedTask;
    }
}
