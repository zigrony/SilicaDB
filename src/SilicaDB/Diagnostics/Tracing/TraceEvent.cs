using System;

namespace SilicaDB.Diagnostics.Tracing
{
    public enum TraceEventType
    {
        Info,
        Started,
        Completed,
        Blocked,
        Error
    }

    public readonly struct TraceEvent
    {
        public long Seq { get; init; }
        public long TsTicks { get; init; }
        public TraceCategory Category { get; init; }
        public TraceEventType Type { get; init; }
        public string Source { get; init; }
        public string? Resource { get; init; }
        public string? Message { get; init; }
        public int? DurationMicros { get; init; }
        public int? ThreadId { get; init; }
        public long UtcTicks { get; init; }

        public static TraceEvent New(
            TraceCategory category,
            TraceEventType type,
            string source,
            string? resource = null,
            string? message = null,
            int? durMicros = null)
        {
            return new TraceEvent
            {
                Seq = 0, // will be assigned by Trace.Emit
                TsTicks = Environment.TickCount64,
                UtcTicks = DateTime.UtcNow.Ticks,
                Category = category,
                Type = type,
                Source = source,
                Resource = resource,
                Message = message,
                DurationMicros = durMicros,
                ThreadId = Environment.CurrentManagedThreadId
            };
        }
    }
}
