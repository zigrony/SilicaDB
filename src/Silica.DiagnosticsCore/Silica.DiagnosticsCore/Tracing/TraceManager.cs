// File: Silica.DiagnosticsCore/Tracing/TraceManager.cs
using System;
using System.Globalization;
using System.Collections.Generic;
using Silica.DiagnosticsCore.Metrics;
using System.Buffers.Binary;

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
            // normalized later alongside NaN handling
            _traceTagValidator = traceTagValidator;
            _minimumLevel = (minimumLevel ?? "trace").Trim().ToLowerInvariant();

            // Pre-register frequently used metrics to avoid silent no-ops
            if (_metrics is not null)
            {
                try
                {
                    if (_metrics is MetricsFacade mf)
                    {
                        mf.TryRegister(DiagCoreMetrics.TracesEmitted);
                        mf.TryRegister(DiagCoreMetrics.TracesDropped);
                        mf.TryRegister(DiagCoreMetrics.TraceTagsDropped);
                        mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    }
                    else
                    {
                        _metrics.Register(DiagCoreMetrics.TracesEmitted);
                        _metrics.Register(DiagCoreMetrics.TracesDropped);
                        _metrics.Register(DiagCoreMetrics.TraceTagsDropped);
                        _metrics.Register(DiagCoreMetrics.IgnoredConfigFieldSet);
                    }
                }
                catch { /* swallow */ }
            }
            var allowed = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
            if (Array.IndexOf(allowed, _minimumLevel) < 0)
            {
                var prev = _minimumLevel;
                _minimumLevel = "trace"; // safe default
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "traces_minimum_level_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, prev));
                }
                catch { /* swallow */ }
            }
            // Normalize sampleRate including NaN
            if (double.IsNaN(sampleRate))
            {
                _sampleRate = 1.0;
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "traces_sample_rate_nan"));
                }
                catch { }
            }
            else
            {
                var clamped = Math.Clamp(sampleRate, 0.0, 1.0);
                _sampleRate = clamped;
                if (!sampleRate.Equals(clamped))
                {
                    try
                    {
                        if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                        _metrics?.Increment(
                            DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "traces_sample_rate_out_of_range"),
                            new KeyValuePair<string, object>(TagKeys.Policy, sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    catch { /* swallow */ }
                }
            }

            // Wire trace tag drop/truncation accounting if a validator is provided.
            if (_traceTagValidator is not null && _metrics is not null)
            {
                _traceTagValidator.SetCallbacks(
                    // NOTE: Do not increment an aggregate "rejected" plus per-cause; that double-counts.
                    // Use per-cause only for TraceTagsDropped, and use TraceTagsTruncated for truncations.
                    onTagRejectedCount: null,
                    onTagTruncatedCount: count =>
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TraceTagsTruncated);
                            _metrics.Increment(DiagCoreMetrics.TraceTagsTruncated.Name, count);
                        }
                        catch { /* swallow */ }
                    },

                    onRejectedInvalidKey: count =>
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TraceTagsDropped);
                            _metrics.Increment(
                                DiagCoreMetrics.TraceTagsDropped.Name, count,
                                new KeyValuePair<string, object>(TagKeys.Field, "invalid_key"));
                        }
                        catch { /* swallow */ }
                    },
                    onRejectedTooMany: count =>
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TraceTagsDropped);
                            _metrics.Increment(
                                DiagCoreMetrics.TraceTagsDropped.Name, count,
                                new KeyValuePair<string, object>(TagKeys.Field, "too_many"));
                        }
                        catch { /* swallow */ }
                    },
                    onRejectedInvalidType: count =>
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TraceTagsDropped);
                            _metrics.Increment(
                                DiagCoreMetrics.TraceTagsDropped.Name, count,
                                new KeyValuePair<string, object>(TagKeys.Field, "invalid_type"));
                        }
                        catch { /* swallow */ }
                    },
                    onRejectedValueTooLong: count =>
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TraceTagsDropped);
                            _metrics.Increment(
                                DiagCoreMetrics.TraceTagsDropped.Name, count,
                                new KeyValuePair<string, object>(TagKeys.Field, "value_too_long"));
                        }
                        catch { /* swallow */ }
                    });

            }

        }

        public void AddAllowedTraceTagKeys(IEnumerable<string> keys)
        {
            if (_traceTagValidator is null)
            {
                _metrics?.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.Field, "dynamic_tag_allowlist_no_validator"));
                return;
            }
            _traceTagValidator.AddAllowedKeys(keys);
            // Optionally record applied config once (different metric), or stay silent to avoid noise.
        }


        private bool ShouldSample(Guid correlationId)
    {
        if (_sampleRate >= 1.0) return true;
        if (_sampleRate <= 0.0) return false;

        Span<byte> b = stackalloc byte[16];
        correlationId.TryWriteBytes(b);

        // Stable 64‑bit hash from GUID bytes (SplitMix64‑style)
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(b);
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
        ulong u = lo ^ (hi + 0x9E3779B97F4A7C15UL);
        u ^= u >> 30; u *= 0xbf58476d1ce4e5b9UL;
        u ^= u >> 27; u *= 0x94d049bb133111ebUL;
        u ^= u >> 31;

        var norm = u / (double)ulong.MaxValue;
        return norm < _sampleRate;
    }

        /// <remarks>
        /// 'status' is treated as severity and must be one of: trace, debug, info, warn, error, fatal
        /// (case-insensitive). Invalid values are rejected and accounted as drops.
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
            => _ = ProcessEmit(component, operation, status, tags, message, exception, correlationId, spanId);

        /// <remarks>
        /// 'status' is treated as severity and must be one of: trace, debug, info, warn, error, fatal
        /// (case-insensitive). Invalid values are rejected and accounted as drops.
        /// </remarks>
        public void Emit(
            string component,
            string operation,
            string status,
            string message,
            Exception? exception = null,
            Guid correlationId = default,
            Guid spanId = default)
            => _ = ProcessEmit(component, operation, status, null, message, exception, correlationId, spanId);

        /// <summary>
        /// Emits if allowed by config/sample/min level; returns true if dispatched, false if dropped.
        /// </summary>
        public bool EmitIfAllowed(
            string component,
            string operation,
            string status,
            IReadOnlyDictionary<string, string>? tags,
            string message,
            Exception? exception = null,
            Guid correlationId = default,
            Guid spanId = default)
            => ProcessEmit(component, operation, status, tags, message, exception, correlationId, spanId);

        private bool ProcessEmit(
            string component,
            string operation,
            string status,
            IReadOnlyDictionary<string, string>? tags,
            string message,
            Exception? exception,
            Guid correlationId,
            Guid spanId)
        {
            var dropComponent = string.IsNullOrWhiteSpace(component) ? _defaultComponent : component;
            var dropOperation = string.IsNullOrWhiteSpace(operation) ? "(none)" : operation;

            if (string.IsNullOrWhiteSpace(operation))
            {
#if DEBUG
                throw new ArgumentException("Operation must not be null or whitespace.", nameof(operation));
#else
                _metrics?.Increment(
                    DiagCoreMetrics.TracesDropped.Name,
                    1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InvalidOperation),
                    new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;
#endif
            }
            if (string.IsNullOrWhiteSpace(status))
            {
#if DEBUG
                throw new ArgumentException("Status must not be null or whitespace.", nameof(status));
#else
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InvalidStatus),
                    new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;
#endif
            }
            if (message is null)
            {
#if DEBUG
                throw new ArgumentNullException(nameof(message));
#else
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.NullMessage),
                    new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;
#endif
            }
            // Validate status against allowed set before normalization
            var sNorm = (status ?? "").Trim().ToLowerInvariant();
            string[] allowedLevels = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
            if (Array.IndexOf(allowedLevels, sNorm) < 0)
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InvalidLevel),
                    new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;
            }
            if (!_enableTracing)
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.Disabled),
                new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;
            }



            // Enforce minimum level (status already validated)
            var normalizedStatus = sNorm;
            if (!IsLevelAllowed(normalizedStatus))            
            {
                // Skip work if metrics are disabled; otherwise account drop
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.BelowMinimum),
                    new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;

            }
            if (correlationId == Guid.Empty)
                correlationId = Guid.NewGuid();

            if (!ShouldSample(correlationId))
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.Sampled),
                new KeyValuePair<string, object>(TagKeys.Component, dropComponent),
                new KeyValuePair<string, object>(TagKeys.Operation, dropOperation));
                return false;

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

            // Ensure exception type tag is present for schema consistency
            if (exception is not null)
            {
                const string exKey = Silica.DiagnosticsCore.Metrics.TagKeys.Exception;
                if (!merged.ContainsKey(exKey))
                    merged[exKey] = exception.GetType().FullName ?? exception.GetType().Name;
            }

            var evtPre = new TraceEvent(
                DateTimeOffset.UtcNow,
                finalComponent,
                operation,
                normalizedStatus,
                merged,
                message,
                exception,
                correlationId,
                spanId);

            // Redact first so any added/changed tags get validated and accounted once.
            var evt = _redactor?.Redact(evtPre) ?? evtPre;
            if (evt is null)
            {
                _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.Redacted),
                    new KeyValuePair<string, object>(TagKeys.Component, finalComponent),
                    new KeyValuePair<string, object>(TagKeys.Operation, operation));
                return false;
            }
            if (_traceTagValidator is not null)
            {
                var kvs = new List<KeyValuePair<string, object>>(evt.Tags.Count);
                foreach (var t in evt.Tags) kvs.Add(new KeyValuePair<string, object>(t.Key, t.Value));
                var validated = _traceTagValidator.Validate(kvs.ToArray());
                var finalTags = new Dictionary<string, string>(validated.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var t in validated)
                    finalTags[t.Key] = System.Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                evt = new TraceEvent(
                    evt.Timestamp, evt.Component, evt.Operation, evt.Status,
                    finalTags, evt.Message, evt.Exception, evt.CorrelationId, evt.SpanId);
            }
            _dispatcher.Dispatch(evt);
            _metrics?.Increment(DiagCoreMetrics.TracesEmitted.Name, 1);
            return true;
        }
        private bool IsLevelAllowed(string status)
        {
            // Map status string to an order index
            string[] order = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
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
                var tags = new Dictionary<string, string> { { TagKeys.Exception, ex.GetType().FullName ?? ex.GetType().Name } };
                Emit(component, operation, "error", tags, message, ex);
                throw;
            }
        }
    }
}
