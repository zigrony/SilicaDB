// File: Silica.DiagnosticsCore/Tracing/Redactors/DefaultTraceRedactor.cs
using System;
using System.Collections.Generic;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Masks sensitive tag values and increments a redacted‑traces metric.
    /// Thread‑safe and allocation‑minimal when no redaction is required.
    /// </summary>
    public sealed class DefaultTraceRedactor : ITraceRedactor
    {
        public const string RedactedPlaceholder = "***";
        private readonly bool _redactMessage;
        private readonly bool _redactExceptionMessage;
        private readonly bool _redactExceptionStack;
        private readonly HashSet<string> _sensitiveKeys;
        private readonly int _maxExceptionStackFrames;
        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;

        public DefaultTraceRedactor(
            IReadOnlyCollection<string>? sensitiveKeys = null,
            Silica.DiagnosticsCore.Metrics.IMetricsManager? metrics = null,
            bool redactMessage = false,
            bool redactExceptionMessage = false,
            bool redactExceptionStack = false,
            int maxExceptionStackFrames = int.MaxValue)
        {
            _sensitiveKeys = sensitiveKeys is null
                            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(sensitiveKeys, StringComparer.OrdinalIgnoreCase);
            _metrics = metrics;
            _redactMessage = redactMessage;
            _redactExceptionMessage = redactExceptionMessage;
            _redactExceptionStack = redactExceptionStack;
            _maxExceptionStackFrames = Math.Max(1, maxExceptionStackFrames);
        }
        public TraceEvent? Redact(TraceEvent traceEvent)
        {
            if (traceEvent is null) throw new ArgumentNullException(nameof(traceEvent));
            var tags = traceEvent.Tags;
            var redactTags = false;
            Dictionary<string,string>? redactedTags = null;
            if (_sensitiveKeys.Count > 0 && tags is { Count: > 0 } &&
                TryBuildRedacted(tags, _sensitiveKeys, out var outTags))
            {
                redactedTags = outTags;
                redactTags = true;
            }

            var newMessage = _redactMessage ? RedactedPlaceholder : traceEvent.Message;
            var newException = traceEvent.Exception;

            bool anyExceptionRedaction = false; // also true when enforcing max stack frames
            if (newException is not null && (_redactExceptionMessage || _redactExceptionStack))
            {
                // Always preserve type name in tags when any exception redaction occurs
                var typeName = newException.GetType().FullName ?? newException.GetType().Name;
                var tagsWithType = redactedTags ?? new Dictionary<string, string>(traceEvent.Tags, StringComparer.OrdinalIgnoreCase);
                tagsWithType[Silica.DiagnosticsCore.Metrics.TagKeys.Exception] = typeName;
                redactedTags = tagsWithType;
            }
            if (_redactExceptionMessage && newException is not null)
            {
                var exType = newException.GetType();
                var hresult = newException.HResult;
                newException = new Exception(RedactedPlaceholder) { HResult = hresult };
                anyExceptionRedaction = true;
            }
            if (_redactExceptionStack && newException is not null)
            {
                // Drop stack trace by constructing a fresh exception with same message
                newException = new Exception(newException.Message);
                anyExceptionRedaction = true;
            }

            // Enforce maximum stack frames even when not fully redacting the stack
            if (newException is not null && !_redactExceptionStack && _maxExceptionStackFrames < int.MaxValue)
            {
                var st = newException.StackTrace;
                if (!string.IsNullOrEmpty(st))
                {
                    // crude split by line; cross-platform tolerant
                    var frames = st.Split('\n');
                    if (frames.Length > _maxExceptionStackFrames)
                    {
                        var msg = newException.Message;
                        newException = new Exception(msg); // rebuild => drops stack
                        anyExceptionRedaction = true;
                        // Ensure exception type tag exists
                        var typeName = traceEvent.Exception?.GetType().FullName ?? traceEvent.Exception?.GetType().Name;
                        if (typeName is not null)
                        {
                            var tagsWithType = redactedTags ?? new Dictionary<string, string>(traceEvent.Tags, StringComparer.OrdinalIgnoreCase);
                            tagsWithType[Silica.DiagnosticsCore.Metrics.TagKeys.Exception] = typeName;
                            redactedTags = tagsWithType;
                        }
                    }
                }
            }
            if (redactTags || _redactMessage || anyExceptionRedaction)
            {
                if (redactTags)
                    _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                        new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.Field, "tags"));
                if (_redactMessage)
                    _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                        new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.Field, "message"));
                if (anyExceptionRedaction)
                    _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                        new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.Field, "exception"));
            }

            if (!redactTags && !_redactMessage && !anyExceptionRedaction)
                return traceEvent; // no changes

            return new TraceEvent(
                traceEvent.Timestamp, traceEvent.Component, traceEvent.Operation, traceEvent.Status,
                redactedTags ?? traceEvent.Tags, newMessage, newException,
                traceEvent.CorrelationId, traceEvent.SpanId);
        }

        private static bool TryBuildRedacted(
            IReadOnlyDictionary<string, string> input,
            IReadOnlyCollection<string> sensitive,
            out Dictionary<string, string> output)
        {
            output = default!;
            bool masked = false;
            foreach (var kv in input)
            {
                if (ContainsInsensitive(sensitive, kv.Key)) { masked = true; break; }
            }
            if (!masked) return false;
            var result = new Dictionary<string, string>(input.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in input)
                result[kv.Key] = ContainsInsensitive(sensitive, kv.Key) ? RedactedPlaceholder : kv.Value;
            output = result;
            return true;
        }

        private static bool ContainsInsensitive(IEnumerable<string> keys, string key)
        {
            foreach (var k in keys)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
