// File: Silica.DiagnosticsCore/Tracing/TraceManager.cs
using System;
using System.Collections.Generic;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Single entry point for producing trace events:
    /// build → validate → redact (optional) → dispatch.
    /// </summary>
    public sealed class TraceManager
    {
        private readonly TraceDispatcher _dispatcher;
        private readonly ITraceRedactor? _redactor;
        private readonly string _defaultComponent;
        private readonly IMetricsManager? _metrics;
        private readonly bool _enableTracing;
        private readonly IReadOnlyDictionary<string, string> _globalTags;
        private readonly double _sampleRate;
        private readonly TagValidator? _traceTagValidator;
        private readonly string _minimumLevel; // normalized lowercase

        public TraceManager(
            TraceDispatcher dispatcher,
            ITraceRedactor? redactor = null,
            string defaultComponent = "default",
            IMetricsManager? metrics = null,
            bool enableTracing = true,
            IReadOnlyDictionary<string, string>? globalTags = null,
            double sampleRate = 1.0,
            TagValidator? traceTagValidator = null,
            string minimumLevel = "trace")
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _redactor = redactor;
            _defaultComponent = string.IsNullOrWhiteSpace(defaultComponent) ? "default" : defaultComponent;
            _metrics = metrics;
            _enableTracing = enableTracing;
            _globalTags = globalTags ?? new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
            _sampleRate = Math.Clamp(sampleRate, 0.0, 1.0);
            _traceTagValidator = traceTagValidator;
            _minimumLevel = (minimumLevel ?? "trace").Trim().ToLowerInvariant();
        }

        public void AddAllowedTraceTagKeys(IEnumerable<string> keys)
        {
            _traceTagValidator?.AddAllowedKeys(keys);
        }

    private bool ShouldSample(Guid correlationId)
        {
            if (_sampleRate >= 1.0) return true;
            if (_sampleRate <= 0.0) return false;

            // Stable normalization: first 4 bytes to UInt32
            var b = correlationId.ToByteArray();
            var u = BitConverter.ToUInt32(b, 0);
            var norm = u / (double)uint.MaxValue;
            return norm < _sampleRate;
        }

        /// <remarks>
        /// 'status' is treated as severity and must be one of: trace, debug, info, warn, error, fatal
        /// (case-insensitive) to participate in MinimumLevel gating. Unknown values default to 'info'.
        /// </remarks>
        public void Emit(
            string component,
            string operation,
            string status,
            IReadOnlyDictionary<string, string>? tags,
            string message,
            Exception? exception = null,
            Guid correlationId = default,
            Guid spanId = default)
            => ProcessEmit(component, operation, status, tags, message, exception, correlationId, spanId);

        /// <remarks>
        /// 'status' is treated as severity and must be one of: trace, debug, info, warn, error, fatal
        /// (case-insensitive) to participate in MinimumLevel gating. Unknown values default to 'info'.
        /// </remarks>
        public void Emit(
            string component,
            string operation,
            string status,
            string message,
            Exception? exception = null,
            Guid correlationId = default,
            Guid spanId = default)
            => ProcessEmit(component, operation, status, null, message, exception, correlationId, spanId);

        private void ProcessEmit(
            string component,
            string operation,
            string status,
            IReadOnlyDictionary<string, string>? tags,
            string message,
            Exception? exception,
            Guid correlationId,
            Guid spanId)
        {
            if (string.IsNullOrWhiteSpace(operation))
                throw new ArgumentException("Operation must not be null or whitespace.", nameof(operation));
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException("Status must not be null or whitespace.", nameof(status));
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            if (!_enableTracing)
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                new KeyValuePair<string, object>(TagKeys.DropCause, "disabled"));
                return;
            }


            // Enforce minimum level
            if (!IsLevelAllowed(status))
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, "below_minimum_level"));
                return;
            }
            if (correlationId == Guid.Empty)
                correlationId = Guid.NewGuid();

            if (!ShouldSample(correlationId))
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                new KeyValuePair<string, object>(TagKeys.DropCause, "sampled"));
                return;
            }
            var finalComponent = string.IsNullOrWhiteSpace(component) ? _defaultComponent : component;

            // Always start with a mutable dictionary
            Dictionary<string, string> merged;
            if (_globalTags.Count == 0)
                merged = tags is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase);
            else
            {
                merged = new Dictionary<string, string>(_globalTags, StringComparer.OrdinalIgnoreCase);
                if (tags is not null)
                    foreach (var kv in tags) merged[kv.Key] = kv.Value; // last-write-wins
            }

            if (_traceTagValidator is not null)
            {
                var kvs = new List<KeyValuePair<string, object>>(merged.Count);
                foreach (var t in merged)
                    kvs.Add(new KeyValuePair<string, object>(t.Key, t.Value));
                var validated = _traceTagValidator.Validate(kvs.ToArray());
                var m2 = new Dictionary<string, string>(validated.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var t in validated)
                    m2[t.Key] = Convert.ToString(t.Value) ?? string.Empty;
                merged = m2;
            }

            var evt = new TraceEvent(
                DateTimeOffset.UtcNow,
                finalComponent,
                operation,
                status,
                merged,
                message,
                exception,
                correlationId,
                spanId);

            if (_redactor != null)
            {
                evt = _redactor.Redact(evt);
                if (evt is null)
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, "redacted"));
                    return;
                }
            }

            _dispatcher.Dispatch(evt);
        }
        private bool IsLevelAllowed(string status)
        {
            // Map status string to an order index
            string[] order = { "trace", "debug", "info", "warn", "error", "fatal" };
            int idxStatus = Array.IndexOf(order, (status ?? "").Trim().ToLowerInvariant());
            int idxMin = Array.IndexOf(order, _minimumLevel);
            if (idxStatus < 0) idxStatus = Array.IndexOf(order, "info"); // or return false
            return idxStatus >= idxMin;
        }
        public void ExecuteWithTrace(string component, string operation, string status, string message, Action action)
        {
            if (!_enableTracing)
            {
                action();
                return;
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var tags = new Dictionary<string, string> { { TagKeys.Exception, ex.GetType().Name } };
                Emit(component, operation, "error", tags, message, ex);
                throw;
            }
        }
    }
}
