using System;
using System.Diagnostics;

namespace Silica.DiagnosticsCore.Tracing
{
    internal sealed class LoggingTraceSink : ITraceSink
    {
        private readonly string _minLevel;

        public LoggingTraceSink(string minLevel)
        {
            _minLevel = (minLevel ?? "trace").Trim().ToLowerInvariant();
        }

        public void Emit(TraceEvent traceEvent)
        {
            if (!IsLevelAllowed(traceEvent.Status))
                return;

            // Format a simple log line
            var msg = $"{traceEvent.Timestamp:O} [{traceEvent.Status}] {traceEvent.Component}/{traceEvent.Operation} " +
                      $"{traceEvent.Message} cid={traceEvent.CorrelationId:D} sid={traceEvent.SpanId:D}";
            // In production, avoid Console.WriteLine to prevent blocking on slow stdout
#if DEBUG
            // Only write to stdout in DEBUG when explicitly enabled
            if (string.Equals(
                Environment.GetEnvironmentVariable("SILICA_DEBUG_CONSOLE_LOG"),
                "1",
                StringComparison.Ordinal))
            {
                Console.WriteLine(msg);
            }
#endif
            Trace.WriteLine(msg);
        }

        private bool IsLevelAllowed(string status)
        {
            string[] order = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
            var s = (status ?? "").Trim().ToLowerInvariant();
            int idxStatus = Array.IndexOf(order, s);
            int idxMin = Array.IndexOf(order, _minLevel);
            if (idxStatus < 0) idxStatus = Array.IndexOf(order, "info");
            return idxStatus >= idxMin;
        }
    }
}
