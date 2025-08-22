// File: Silica.DiagnosticsCore/Tracing/ITraceSink.cs
using System;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Contract for a component that consumes trace events.
    /// Implementations should forward events to loggers, aggregators,
    /// distributed tracing backends, or in‑memory buffers for testing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations MUST be thread‑safe.
    /// </para>
    /// <para>
    /// Emission should never block the caller; use internal buffering/queueing
    /// for asynchronous processing if needed.
    /// </para>
    /// <para>
    /// Events are expected to already be schema‑valid (e.g., tags validated/redacted)
    /// before they reach a sink; sinks should not mutate data beyond transport encoding.
    /// </para>
    /// </remarks>
    public interface ITraceSink
    {
        /// <summary>
        /// Emits a trace event to the sink.
        /// Implementations must accept valid, non‑null <paramref name="traceEvent"/> instances.
        /// </summary>
        /// <param name="traceEvent">The event to emit. Must not be null.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="traceEvent"/> is null.
        /// </exception>
        void Emit(TraceEvent traceEvent);
    }
}
