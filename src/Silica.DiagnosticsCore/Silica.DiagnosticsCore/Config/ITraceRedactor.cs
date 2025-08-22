// File: Silica.DiagnosticsCore/Tracing/ITraceRedactor.cs
using System;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Defines the contract for redacting sensitive information from trace events
    /// prior to emission to any sinks or subscribers.
    /// </summary>
    /// <remarks>
    /// Implementations must be:
    /// <list type="bullet">
    /// <item><description><b>Pure</b> - no mutation of the incoming instance; always
    ///   return the same output for the same input.</description></item>
    /// <item><description><b>Safe</b> - handle null or malformed fields gracefully;
    ///   never throw from <see cref="Redact"/> except for <see cref="ArgumentNullException"/> when
    ///   <paramref name="traceEvent"/> itself is null.</description></item>
    /// <item><description><b>Deterministic</b> - avoid randomness so that redaction
    ///   behaviour is testable and repeatable.</description></item>
    /// </list>
    /// </remarks>
    public interface ITraceRedactor
    {
        /// <summary>
        /// Applies redaction rules to the specified trace event.
        /// </summary>
        /// <param name="traceEvent">The original trace event. Must not be null.</param>
        /// <returns>
        /// A <see cref="TraceEvent"/> instance with redacted values;
        /// the original instance if no changes are required;
        /// or <c>null</c> to drop the event entirely.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="traceEvent"/> is null.
        /// </exception>
        TraceEvent? Redact(TraceEvent traceEvent);
    }
}
