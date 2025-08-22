using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// An in‑memory implementation of <see cref="ITraceSink"/> that stores all received
    /// trace events for later inspection. Intended for testing, diagnostics, and
    /// scenarios where events need to be examined without exporting.
    /// </summary>
    public sealed class InMemoryTraceSink : ITraceSink
    {
        private readonly ConcurrentQueue<TraceEvent> _events = new();

        /// <summary>
        /// Gets a snapshot of all events currently stored in this sink.
        /// </summary>
        public IReadOnlyCollection<TraceEvent> Events => _events.ToArray();

        /// <summary>
        /// Adds the given trace event to the in‑memory store.
        /// </summary>
        /// <param name="traceEvent">The event to store. Must not be null.</param>
        public void Emit(TraceEvent traceEvent)
        {
            if (traceEvent == null) throw new ArgumentNullException(nameof(traceEvent));
            _events.Enqueue(traceEvent);
        }

        /// <summary>
        /// Attempts to remove all stored events from the sink.
        /// </summary>
        public void Clear()
        {
            while (_events.TryDequeue(out _)) { }
        }
    }
}
