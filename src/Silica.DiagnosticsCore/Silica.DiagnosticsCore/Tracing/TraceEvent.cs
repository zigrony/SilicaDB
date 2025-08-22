// File: Silica.DiagnosticsCore/Tracing/TraceEvent.cs
using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Represents a single trace event emitted by a component or subsystem.
    /// Immutable to ensure thread safety and consistency in concurrent pipelines.
    /// </summary>
    public sealed class TraceEvent
    {
        /// <summary>UTC timestamp when the event was created.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Identifies the emitting component or subsystem (e.g., Storage, WAL, BufferPool).</summary>
        public string Component { get; }

        /// <summary>The logical operation being performed (e.g., Read, Write, Evict).</summary>
        public string Operation { get; }

        /// <summary>The outcome status of the operation (e.g., Success, Error).</summary>
        public string Status { get; }

        /// <summary>
        /// Arbitrary key/value tags providing additional event context.
        /// Keys should follow the audited tag key contract to ensure consistency.
        /// </summary>
        public IReadOnlyDictionary<string, string> Tags { get; }

        /// <summary>A descriptive, human‑readable message for the event.</summary>
        public string Message { get; }

        /// <summary>Optional exception associated with the event.</summary>
        public Exception? Exception { get; }

        /// <summary>Correlation identifier to group related events across components.</summary>
        public Guid CorrelationId { get; }

        /// <summary>Identifier for the specific span this event belongs to, if applicable.</summary>
        public Guid SpanId { get; }

        public TraceEvent(
            DateTimeOffset timestamp,
            string component,
            string operation,
            string status,
            IReadOnlyDictionary<string, string>? tags,
            string? message,
            Exception? exception,
            Guid correlationId,
            Guid spanId)
        {
            Timestamp = timestamp;
            Component = !string.IsNullOrWhiteSpace(component)
                ? component
                : throw new ArgumentNullException(nameof(component));

            Operation = !string.IsNullOrWhiteSpace(operation)
                ? operation
                : throw new ArgumentNullException(nameof(operation));

            Status = status ?? string.Empty;
            Tags = tags ?? new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
            Message = message ?? string.Empty;
            Exception = exception;
            CorrelationId = correlationId != Guid.Empty ? correlationId : Guid.NewGuid();
            SpanId = spanId != Guid.Empty ? spanId : Guid.NewGuid();
        }

        /// <summary>
        /// Returns a new <see cref="TraceEvent"/> with the specified tag added or updated.
        /// Tag keys are case‑insensitive; values overwrite existing ones for the same key.
        /// </summary>
        public TraceEvent WithTag(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Tag key must not be null or whitespace.", nameof(key));

            var cloneTags = new Dictionary<string, string>(Tags, StringComparer.OrdinalIgnoreCase)
            {
                [key] = value
            };

            return new TraceEvent(
                Timestamp,
                Component,
                Operation,
                Status,
                cloneTags,
                Message,
                Exception,
                CorrelationId,
                SpanId);
        }
    }
}
