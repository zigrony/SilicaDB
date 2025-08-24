// File: Silica.DiagnosticsCore/Tracing/Redactors/DefaultTraceRedactor.cs
using System;
using System.Collections;
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
        private readonly int _maxInnerExceptionDepth;
        private readonly int _maxMessageLength;
        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;

        public DefaultTraceRedactor(
            IReadOnlyCollection<string>? sensitiveKeys = null,
            Silica.DiagnosticsCore.Metrics.IMetricsManager? metrics = null,
            bool redactMessage = false,
            bool redactExceptionMessage = false,
            bool redactExceptionStack = false,
            int maxExceptionStackFrames = int.MaxValue,
            int maxInnerExceptionDepth = int.MaxValue,
            int maxMessageLength = int.MaxValue)
        {
            _sensitiveKeys = sensitiveKeys is null
                            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(sensitiveKeys, StringComparer.OrdinalIgnoreCase);
            _metrics = metrics;
            _redactMessage = redactMessage;
            _redactExceptionMessage = redactExceptionMessage;
            _redactExceptionStack = redactExceptionStack;
            _maxExceptionStackFrames = Math.Max(1, maxExceptionStackFrames);
            _maxInnerExceptionDepth = Math.Max(0, maxInnerExceptionDepth);
            _maxMessageLength = Math.Max(1, maxMessageLength);

            // Ensure the redaction counter exists so increments never silently no-op
            try
            {
                if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TracesRedacted);
                else _metrics?.Register(DiagCoreMetrics.TracesRedacted);
            }
            catch
            {
                // Swallow registration/exporter issues to keep construction safe.
            }

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
            // Optional message length enforcement (truncate, not redact)
            if (!_redactMessage && newMessage is not null && newMessage.Length > _maxMessageLength)
            {
                try
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                        new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.Field, "message_truncated"));
                }
                catch { /* swallow */ }
                newMessage = newMessage[.._maxMessageLength];
            }
            var newException = traceEvent.Exception;
            // Preserve original exception type name for tagging regardless of cloning/redaction
            var exType = traceEvent.Exception?.GetType();
            var originalExceptionTypeName = exType?.FullName ?? exType?.Name ?? "Exception";
            bool anyExceptionRedaction = false; // true when message/stack redacted or frames enforcement observed
            bool stackTruncatedAccounted = false; // prevent duplicate accounting across branches
            if (newException is not null && (_redactExceptionMessage || _redactExceptionStack))
            {
                // Always preserve original exception type name when any exception redaction occurs
                var tagsWithType = redactedTags ?? new Dictionary<string, string>(traceEvent.Tags, StringComparer.OrdinalIgnoreCase);
                tagsWithType[Silica.DiagnosticsCore.Metrics.TagKeys.Exception] = originalExceptionTypeName;
                redactedTags = tagsWithType;

                var sanitizedMessage = _redactExceptionMessage ? RedactedPlaceholder : newException.Message;
                var sanitized = new Exception(sanitizedMessage)
                {
                    HResult = newException.HResult,
                    Source = newException.Source
                };
                // preserve Data
                foreach (DictionaryEntry kv in newException.Data)
                    sanitized.Data[kv.Key] = kv.Value;
                // preserve inner chain up to configured inner exception depth
                if (newException.InnerException is not null)
                {
                    sanitized = CloneWithInnerChain(sanitized, newException.InnerException, _maxInnerExceptionDepth);
                }
                // Replace exception if either message or stack must be redacted.
                // Note: replacing the exception loses the original stack trace.
                // We surface this tradeoff explicitly via metrics when only the message is redacted.
                var replacingImpliesStackLoss = !_redactExceptionStack && _redactExceptionMessage;
                if (_redactExceptionMessage || _redactExceptionStack)
                {
                    newException = sanitized;
                    anyExceptionRedaction = true;
                    if (replacingImpliesStackLoss)
                    {
                        try
                        {
                            _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                                new KeyValuePair<string, object>(TagKeys.Field, "exception_stack_dropped"));
                        }
                        catch { /* swallow */ }
                    }
                }
            }
            // Enforce maximum stack frames even when not fully redacting the stack
            if (newException is not null && !_redactExceptionStack && _maxExceptionStackFrames < int.MaxValue)            
            {
                var st = newException.StackTrace;
                if (!string.IsNullOrEmpty(st))
                {
                    // crude split by line; cross-platform tolerant
                    var frames = st.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    if (frames.Length > _maxExceptionStackFrames)
                    {
                        // Specific accounting for stack truncation by frame limit; do not replace exception object
                        anyExceptionRedaction = true;
                        if (!stackTruncatedAccounted)
                        {
                            stackTruncatedAccounted = true;
                            try
                            {
                                _metrics?.Increment(DiagCoreMetrics.TracesRedacted.Name, 1,
                                    new KeyValuePair<string, object>(TagKeys.Field, "exception_stack_truncated"));
                            }
                            catch { /* swallow */ }
                        }
                        // Ensure original exception type tag exists
                        var tagsWithType2 = redactedTags ?? new Dictionary<string, string>(traceEvent.Tags, StringComparer.OrdinalIgnoreCase);
                        tagsWithType2[Silica.DiagnosticsCore.Metrics.TagKeys.Exception] = originalExceptionTypeName;
                        redactedTags = tagsWithType2;
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
                // Only raise generic "exception" when we actually redact message/stack.
                if (anyExceptionRedaction && (_redactExceptionMessage || _redactExceptionStack))
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
            // Fast path: if no sensitive keys present, nothing to do.
            var sensitiveSet = sensitive as HashSet<string> ?? new HashSet<string>(sensitive, StringComparer.OrdinalIgnoreCase);
            bool masked = input.Keys.Any(k => sensitiveSet.Contains(k));
            if (!masked) return false;
            var result = new Dictionary<string, string>(input.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in input)
                result[kv.Key] = sensitiveSet.Contains(kv.Key) ? RedactedPlaceholder : kv.Value;
            output = result;
            return true;
        }

        // Replace CloneWithInnerChain with:
        private static Exception CloneWithInnerChain(Exception root, Exception? inner, int maxDepth)
        {
            // Collect shallow->deep up to maxDepth, then build deep->shallow to preserve original order
            if (maxDepth <= 0 || inner is null)
            {
                var top = new Exception(root.Message)
                {
                    HResult = root.HResult,
                    Source = root.Source
                };
                foreach (DictionaryEntry kv in root.Data) top.Data[kv.Key] = kv.Value;
                return top;
            }

            var chain = new List<Exception>(8);
            var cur = inner;
            int depth = 0;
            while (cur is not null && depth < maxDepth)
            {
                chain.Add(cur);
                cur = cur.InnerException;
                depth++;
            }

            Exception? built = null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var src = chain[i];
                var next = new Exception(src.Message, built)
                {
                    HResult = src.HResult,
                    Source = src.Source
                };
                foreach (DictionaryEntry kv in src.Data) next.Data[kv.Key] = kv.Value;
                built = next;
            }

            var rebuiltTop = new Exception(root.Message, built)
            {
                HResult = root.HResult,
                Source = root.Source
            };
            foreach (DictionaryEntry kv in root.Data) rebuiltTop.Data[kv.Key] = kv.Value;
            return rebuiltTop;
        }

    }
}
