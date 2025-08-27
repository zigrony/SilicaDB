// File: Silica.DiagnosticsCore/Tracing/Redactors/CompositeTraceRedactor.cs
using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Executes multiple <see cref="ITraceRedactor"/> instances in order against a trace event.
    /// Each redactor receives the output of the previous stage.
    /// </summary>
    /// <remarks>
    /// - Null input throws immediately - redactors should be fed only valid events.
    /// - The chain terminates early if any stage returns null (drop semantics).
    /// - Immutable chain: order and membership are fixed after construction.
    /// </remarks>
    public sealed class CompositeTraceRedactor : ITraceRedactor
    {
        private readonly IReadOnlyList<ITraceRedactor> _redactors;

        /// <param name="redactors">
        /// The redactors to compose. Must be non‑null and contain at least one element.
        /// </param>
        /// <exception cref="ArgumentNullException">If <paramref name="redactors"/> is null.</exception>
        /// <exception cref="ArgumentException">If no redactors are provided.</exception>
        public CompositeTraceRedactor(IEnumerable<ITraceRedactor> redactors)
        {
            if (redactors is null)
                throw new ArgumentNullException(nameof(redactors));

            IList<ITraceRedactor>? list = redactors as IList<ITraceRedactor>;
            if (list == null)
            {
                var tmp = new List<ITraceRedactor>();
                foreach (var r in redactors)
                    tmp.Add(r);
                list = tmp;
            }
            if (list.Count == 0)
                throw new ArgumentException("At least one redactor must be provided.", nameof(redactors));

            // Defensive copy to lock in order & membership
            _redactors = new List<ITraceRedactor>(list).AsReadOnly();
        }

        /// <inheritdoc />
        public TraceEvent? Redact(TraceEvent trace)
        {
            if (trace is null)
                throw new ArgumentNullException(nameof(trace));

            var current = trace;
            foreach (var redactor in _redactors)
            {
                current = redactor.Redact(current);
                if (current is null)
                    break; // early termination if a stage drops the event
            }
            return current;
        }
    }
}
