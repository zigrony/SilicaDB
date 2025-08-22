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
            var msg = $"{DateTimeOffset.UtcNow:O} [{traceEvent.Status}] {traceEvent.Component}/{traceEvent.Operation} " +
                      $"{traceEvent.Message} cid={traceEvent.CorrelationId:D} sid={traceEvent.SpanId:D}";
            // In production, avoid Console.WriteLine to prevent blocking on slow stdout
#if DEBUG
            Console.WriteLine(msg);
#endif
            Trace.WriteLine(msg);
        }

        private bool IsLevelAllowed(string status)
        {
            string[] order = { "trace", "debug", "info", "warn", "error", "fatal" };
            int idxStatus = Array.IndexOf(order, (status ?? "").Trim().ToLowerInvariant());
            int idxMin = Array.IndexOf(order, _minLevel);
            if (idxStatus < 0) idxStatus = Array.IndexOf(order, "info");
            return idxStatus >= idxMin;
        }
    }
}
