// File: Silica.DiagnosticsCore/DiagnosticsOptions.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Channels;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Internal;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Canonical configuration for diagnostics: metrics, tracing, and instrumentation.
    /// Mutable for construction, then Freeze() to normalize and lock values.
    /// </summary>
    public sealed class DiagnosticsOptions
    {
        /// <summary>
        /// Additional allowed metric tag keys beyond the base set. Intended for low-cardinality, schema-governed tags.
        /// </summary>
        public IEnumerable<string> AllowedCustomMetricTagKeys { get; set; } = Array.Empty<string>();

        public bool StrictBootstrapOptions { get; set; } = true;
        // ----- Defaults and allowed sets -----
        public static class Defaults
        {
            public const string DefaultComponent = "default";
            public const string MinimumLevel = "info"; // allowed: trace, debug, info, warn, error, fatal
            public const int InMemoryCapacity = 10_000;

#if DEBUG
            public const bool LoggingAvailable = true;
#else
            public const bool LoggingAvailable = false;
#endif
 
            [Obsolete("UseHighResolutionTiming is not enforced by the pipeline and will be removed in a future release.")]
            public const bool UseHighResolutionTiming = true;

            [Obsolete("CaptureExceptions is not enforced by the pipeline and will be removed in a future release. Use redaction options instead.")]
            public const bool CaptureExceptions = true;

            public const bool EnableTracing = true;
            public const bool EnableMetrics = true;

#if DEBUG
            public const bool EnableLogging = true;
#else
    public const bool EnableLogging = false;
#endif

            public const bool StrictMetrics = true;
            public const int MaxTagsPerEvent = 10;
            public const int MaxTagValueLength = 64;
            public const int MaxTraceBuffer = 8192;
            public const double EffectiveTraceSampleRate = 1.0; // when TraceSampleRate is null
            public const int DispatcherQueueCapacity = 1024;
            public const int ShutdownTimeoutMs = 5000;
            public const BoundedChannelFullMode DispatcherFullMode = BoundedChannelFullMode.DropWrite;
            public const bool RedactTraceMessage = false;
            public const bool RedactExceptionMessage = false;
            public const bool RedactExceptionStack = false;
            public const int MaxExceptionStackFrames = int.MaxValue;
        }

        private bool _redactTraceMessage = Defaults.RedactTraceMessage;
        private bool _redactExceptionMessage = Defaults.RedactExceptionMessage;
        private bool _redactExceptionStack = Defaults.RedactExceptionStack;
        private int _maxExceptionStackFrames = Defaults.MaxExceptionStackFrames;
        public bool RedactTraceMessage { get => _redactTraceMessage; set { ThrowIfFrozen(); _redactTraceMessage = value; } }
        public bool RedactExceptionMessage { get => _redactExceptionMessage; set { ThrowIfFrozen(); _redactExceptionMessage = value; } }
        public bool RedactExceptionStack { get => _redactExceptionStack; set { ThrowIfFrozen(); _redactExceptionStack = value; } }
        public int MaxExceptionStackFrames
        {
            get => _maxExceptionStackFrames;
            set { ThrowIfFrozen(); _maxExceptionStackFrames = Math.Max(1, value); }
        }

        public BoundedChannelFullMode DispatcherFullMode { get; set; } = Defaults.DispatcherFullMode;
        [Obsolete("The IMetricsManager parameter is ignored; use DiagnosticsCoreBootstrap.Start to wire metrics.")]
        public DiagnosticsOptions(IMetricsManager? metrics = null) { }
        /// <summary>
        /// Stable fingerprint of effective, normalized values for drift detection and auditing.
        /// Safe to call before or after Freeze(); values are taken from effective state.
        /// </summary>
        public string ComputeFingerprint()
        {
            var sb = new StringBuilder();

            // Normalize as Freeze() would
            var normalizedComponent = string.IsNullOrWhiteSpace(_defaultComponent)
                ? "unknown"
                : _defaultComponent.Trim();

            var normalizedLevel = (_minimumLevel ?? string.Empty).Trim().ToLowerInvariant();
            var effectiveEnableLogging = Defaults.LoggingAvailable && _enableLogging;

            sb.Append("DefaultComponent=").Append(normalizedComponent).Append(';');
            sb.Append("MinimumLevel=").Append(normalizedLevel).Append(';');
            sb.Append("EnableLogging=").Append(effectiveEnableLogging).Append(';');
            sb.Append("EnableTracing=").Append(_enableTracing).Append(';');
            sb.Append("EnableMetrics=").Append(_enableMetrics).Append(';');
            sb.Append("StrictMetrics=").Append(_strictMetrics).Append(';');
            sb.Append("MaxTagsPerEvent=").Append(_maxTagsPerEvent).Append(';');
            sb.Append("MaxTagValueLength=").Append(_maxTagValueLength).Append(';');
            sb.Append("MaxTraceBuffer=").Append(_maxTraceBuffer).Append(';');
            sb.Append("QueueCapacity=").Append(_dispatcherQueueCapacity).Append(';');
            sb.Append("ShutdownMs=").Append(_shutdownTimeoutMs).Append(';');
            sb.Append("TraceSampleRate=").Append(
                EffectiveTraceSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ).Append(';');
            sb.Append("DispatcherFullMode=").Append((int)DispatcherFullMode).Append(';');
            sb.Append("RedactTraceMessage=").Append(_redactTraceMessage).Append(';');
            sb.Append("RedactExceptionMessage=").Append(_redactExceptionMessage).Append(';');
            sb.Append("RedactExceptionStack=").Append(_redactExceptionStack).Append(';');
            sb.Append("MaxExceptionStackFrames=").Append(
                _maxExceptionStackFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ).Append(';');
            sb.Append("StrictBootstrapOptions=").Append(StrictBootstrapOptions).Append(';');
            sb.Append("StrictGlobalTagKeys=").Append(StrictGlobalTagKeys).Append(';');

            if (AllowedCustomGlobalTagKeys is not null)
                foreach (var k in new SortedSet<string>(AllowedCustomGlobalTagKeys, StringComparer.OrdinalIgnoreCase))
                    sb.Append("AK=").Append(k).Append(';');

            if (_globalTags is { Count: > 0 })
                foreach (var kv in new SortedDictionary<string, string>(_globalTags, StringComparer.OrdinalIgnoreCase))
                    sb.Append("GT[").Append(kv.Key).Append("]=").Append(kv.Value).Append(';');

            if (_sensitiveTagKeys is { Count: > 0 })
                foreach (var k in new SortedSet<string>(_sensitiveTagKeys, StringComparer.OrdinalIgnoreCase))
                    sb.Append("SK=").Append(k).Append(';');

            if (AllowedCustomMetricTagKeys is not null)
                foreach (var k in new SortedSet<string>(AllowedCustomMetricTagKeys, StringComparer.OrdinalIgnoreCase))
                    sb.Append("MK=").Append(k).Append(';');

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private int _shutdownTimeoutMs = Defaults.ShutdownTimeoutMs;
        public int ShutdownTimeoutMs
        {
            get => _shutdownTimeoutMs;
            set { ThrowIfFrozen(); _shutdownTimeoutMs = Math.Max(0, value); }
        }
        private int _dispatcherQueueCapacity = Defaults.DispatcherQueueCapacity;
        public int DispatcherQueueCapacity
        {
            get => _dispatcherQueueCapacity;
            set { ThrowIfFrozen(); _dispatcherQueueCapacity = value; }
        }
        private static readonly string[] AllowedLevels = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;

        // ----- Backing fields -----
        private string _defaultComponent = Defaults.DefaultComponent;
        private string _minimumLevel = Defaults.MinimumLevel;

        private Dictionary<string, string> _globalTags = new(StringComparer.OrdinalIgnoreCase);
        private ReadOnlyDictionary<string, string>? _globalTagsRo;

        private int _inMemoryCapacity = Defaults.InMemoryCapacity;

        private bool _useHighResolutionTiming = Defaults.UseHighResolutionTiming;
        private bool _captureExceptions = Defaults.CaptureExceptions;

        private bool _enableTracing = Defaults.EnableTracing;
        private bool _enableMetrics = Defaults.EnableMetrics;
        private bool _enableLogging = Defaults.EnableLogging;

        private bool _strictMetrics = Defaults.StrictMetrics;
        private int _maxTagsPerEvent = Defaults.MaxTagsPerEvent;
        private int _maxTagValueLength = Defaults.MaxTagValueLength;
        private int _maxTraceBuffer = Defaults.MaxTraceBuffer;

        private double? _traceSampleRate = null; // null => Defaults.EffectiveTraceSampleRate
        private HashSet<string> _sensitiveTagKeys = new(StringComparer.OrdinalIgnoreCase);
        private ReadOnlyCollection<string>? _sensitiveTagKeysRo;

        private volatile bool _frozen;

        /// <summary>
        /// If true, GlobalTags keys must be in the base allowed set or explicitly allow‑listed.
        /// </summary>
        public bool StrictGlobalTagKeys { get; set; } = false;

        /// <summary>
        /// Additional allowed global tag keys when <see cref="StrictGlobalTagKeys"/> is true.
        /// </summary>
        public IEnumerable<string> AllowedCustomGlobalTagKeys { get; set; } = Array.Empty<string>();
        // ----- Properties (guarded by Freeze) -----

        /// <summary>
        /// Default component name used when call sites do not specify one.
        /// Normalized to non-empty and trimmed during Freeze(). Fallback: "unknown".
        /// </summary>
        public string DefaultComponent
        {
            get => _defaultComponent;
            set { ThrowIfFrozen(); _defaultComponent = value ?? string.Empty; }
        }

        /// <summary>
        /// Minimum severity level for emission. Allowed: trace, debug, info, warn, error, fatal (case-insensitive).
        /// Normalized to lowercase during Freeze().
        /// </summary>
        public string MinimumLevel
        {
            get => _minimumLevel;
            set { ThrowIfFrozen(); _minimumLevel = value ?? string.Empty; }
        }

        /// <summary>
        /// Globally-applied tags for all metrics and traces. Keys are case-insensitive.
        /// Validated and copied into an immutable view during Freeze().
        /// </summary>
        public IDictionary<string, string> GlobalTags
        {
            get => _globalTags;
            set
            {
                ThrowIfFrozen();
                _globalTags = value is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
                _globalTagsRo = null;
            }
        }

        /// <summary>
        /// Maximum number of in-memory items retained by in-memory sinks.
        /// Deprecated in favor of MaxTraceBuffer; if set explicitly and MaxTraceBuffer is default,
        /// Freeze() will map this value into MaxTraceBuffer.
        /// </summary>
        [Obsolete("Use MaxTraceBuffer instead. Freeze() maps InMemoryCapacity to MaxTraceBuffer if the latter is default.", false)]
        public int InMemoryCapacity
        {
            get => _inMemoryCapacity;
            set { ThrowIfFrozen(); _inMemoryCapacity = value; }
        }

        /// <summary>
        /// Use Stopwatch for high-resolution instrumentation.
        /// </summary>
        [Obsolete("UseHighResolutionTiming is not enforced at runtime and will be removed in a future release.")]
        public bool UseHighResolutionTiming
        {
            get => _useHighResolutionTiming;
            set { ThrowIfFrozen(); _useHighResolutionTiming = value; }
        }

        /// <summary>
        /// Capture exceptions and attach to emitted events.
        /// </summary>
        [Obsolete("CaptureExceptions is not enforced at runtime and will be removed in a future release. Use redaction options instead.")]
        public bool CaptureExceptions
        {
            get => _captureExceptions;
            set { ThrowIfFrozen(); _captureExceptions = value; }
        }

        /// <summary>
        /// Enable or disable tracing globally.
        /// </summary>
        public bool EnableTracing
        {
            get => _enableTracing;
            set { ThrowIfFrozen(); _enableTracing = value; }
        }

        /// <summary>
        /// Enable or disable metrics globally.
        /// </summary>
        public bool EnableMetrics
        {
            get => _enableMetrics;
            set { ThrowIfFrozen(); _enableMetrics = value; }
        }

        /// <summary>
        /// Enable or disable logging globally.
        /// </summary>
        public bool EnableLogging
        {
            get => _enableLogging;
            set { ThrowIfFrozen(); _enableLogging = value; }
        }

        /// <summary>
        /// If true, metric name/tag violations cause drops with accounting; otherwise attempts are best-effort.
        /// </summary>
        public bool StrictMetrics
        {
            get => _strictMetrics;
            set { ThrowIfFrozen(); _strictMetrics = value; }
        }

        /// <summary>
        /// Maximum number of tags allowed per event (including global tags).
        /// </summary>
        public int MaxTagsPerEvent
        {
            get => _maxTagsPerEvent;
            set { ThrowIfFrozen(); _maxTagsPerEvent = value; }
        }

        /// <summary>
        /// Maximum allowed length for any tag value.
        /// </summary>
        public int MaxTagValueLength
        {
            get => _maxTagValueLength;
            set { ThrowIfFrozen(); _maxTagValueLength = value; }
        }

        /// <summary>
        /// Maximum bounded capacity for the in-memory trace sink.
        /// </summary>
        public int MaxTraceBuffer
        {
            get => _maxTraceBuffer;
            set { ThrowIfFrozen(); _maxTraceBuffer = value; }
        }

        /// <summary>
        /// Optional sampling rate for traces [0.0, 1.0]. Null means 1.0 (no sampling).
        /// </summary>
        public double? TraceSampleRate
        {
            get => _traceSampleRate;
            set { ThrowIfFrozen(); _traceSampleRate = value; }
        }

        /// <summary>
        /// Keys considered sensitive and subject to redaction. Case-insensitive.
        /// Normalized to an immutable view during Freeze().
        /// </summary>
        public IEnumerable<string> SensitiveTagKeys
        {
            get => _sensitiveTagKeys;
            set
            {
                ThrowIfFrozen();
                _sensitiveTagKeys = value is null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
                _sensitiveTagKeysRo = null;
            }
        }

        // ----- Effective/derived views (available pre- or post-Freeze) -----

        /// <summary>
        /// Effective sampling rate; returns Defaults.EffectiveTraceSampleRate when TraceSampleRate is null.
        /// </summary>
        public double EffectiveTraceSampleRate => _traceSampleRate ?? Defaults.EffectiveTraceSampleRate;

        /// <summary>
        /// Read-only, case-insensitive view of global tags. When frozen, this is stable.
        /// </summary>
        public IReadOnlyDictionary<string, string> EffectiveGlobalTags
        {
            get
            {
                if (_globalTagsRo is null)
                    _globalTagsRo = new ReadOnlyDictionary<string, string>(_globalTags);
                return _globalTagsRo;
            }
        }

        /// <summary>
        /// Read-only, case-insensitive view of sensitive tag keys. When frozen, this is stable.
        /// </summary>
        public IReadOnlyCollection<string> SensitiveTagKeysReadonly
        {
            get
            {
                if (_sensitiveTagKeysRo is null)
                    _sensitiveTagKeysRo = new ReadOnlyCollection<string>(new List<string>(_sensitiveTagKeys));
                return _sensitiveTagKeysRo;
            }
        }

        /// <summary>
        /// Indicates whether the options were frozen via Freeze().
        /// </summary>
        public bool IsFrozen => _frozen;

        // ----- Validation + normalization -----

        /// <summary>
        /// Validate current values and throw if invalid. Does not modify state.
        /// Set validateGlobalTagsAgainstLimits=false to skip checking GlobalTags against MaxTagsPerEvent/MaxTagValueLength.
        /// </summary>
        public DiagnosticsOptions ValidateOrThrow(bool validateGlobalTagsAgainstLimits = true)
        {
            if (_dispatcherQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(DispatcherQueueCapacity), "Must be > 0.");

            // Basic numeric ranges
            if (_maxTagsPerEvent < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTagsPerEvent), "Must be >= 0.");
            if (_maxTagValueLength < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxTagValueLength), "Must be >= 1.");
            if (_maxTraceBuffer <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTraceBuffer), "Must be > 0.");
            if (_inMemoryCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(InMemoryCapacity), "Must be >= 0.");

            // Trace sample rate range if present
            if (_traceSampleRate is double rate && (rate < 0.0 || rate > 1.0))
                throw new ArgumentOutOfRangeException(nameof(TraceSampleRate), "Must be between 0.0 and 1.0.");

            // Minimum level allowed set (case-insensitive)
            var lvl = (_minimumLevel ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(AllowedLevels, lvl) < 0)
                throw new ArgumentException($"MinimumLevel must be one of: {string.Join(", ", AllowedLevels)}.", nameof(MinimumLevel));

            if (validateGlobalTagsAgainstLimits)
            {
                if (_globalTags.Count > _maxTagsPerEvent)
                    throw new ArgumentException($"GlobalTags count {_globalTags.Count} exceeds MaxTagsPerEvent ({_maxTagsPerEvent}).", nameof(GlobalTags));

                foreach (var kvp in _globalTags)
                {
                    if (kvp.Key is null)
                        throw new ArgumentException("GlobalTags contains a null key.", nameof(GlobalTags));
                    if (kvp.Value is null)
                        throw new ArgumentException($"GlobalTags[\"{kvp.Key}\"] has a null value.", nameof(GlobalTags));
                    if (_maxTagValueLength >= 0 && kvp.Value.Length > _maxTagValueLength)
                        throw new ArgumentException($"GlobalTags[\"{kvp.Key}\"] value length {kvp.Value.Length} exceeds MaxTagValueLength ({_maxTagValueLength}).", nameof(GlobalTags));
                    if (StrictGlobalTagKeys)
                    {
                        var baseKeys = new HashSet<string>(new[]{
                            TagKeys.Pool, TagKeys.Device, TagKeys.Operation, TagKeys.Component,
                            TagKeys.Tenant, TagKeys.Status, TagKeys.Exception, TagKeys.Shard,
                            TagKeys.Thread, TagKeys.Concurrency, TagKeys.DropCause,
                            TagKeys.Region, TagKeys.WidgetId, TagKeys.Policy, TagKeys.Sink, TagKeys.Field
                        }, StringComparer.OrdinalIgnoreCase);
                        baseKeys.UnionWith(AllowedCustomGlobalTagKeys ?? Array.Empty<string>());
                        if (!baseKeys.Contains(kvp.Key))
                            throw new ArgumentException($"GlobalTags key '{kvp.Key}' not allowed under StrictGlobalTagKeys.", nameof(GlobalTags));
                    }
                }
            }

            return this;
        }

        /// <summary>
        /// Normalize values (trim, lowercase where applicable, map deprecated fields), validate, and make the instance immutable.
        /// Safe to call multiple times.
        /// </summary>
        public DiagnosticsOptions Freeze()
        {
            if (_frozen) return this;

            // Map deprecated capacity if MaxTraceBuffer is still default
            if (_maxTraceBuffer == Defaults.MaxTraceBuffer && _inMemoryCapacity != Defaults.InMemoryCapacity)
            {
                _maxTraceBuffer = _inMemoryCapacity;
            }

            // Normalize component + level
            _defaultComponent = string.IsNullOrWhiteSpace(_defaultComponent)
                ? "unknown"
                : _defaultComponent.Trim();

            _minimumLevel = (_minimumLevel ?? string.Empty).Trim().ToLowerInvariant();

            // Normalize tags dictionary (copy to ensure internal consistency)
            if (!ReferenceEquals(_globalTags.Comparer, StringComparer.OrdinalIgnoreCase))
                _globalTags = new Dictionary<string, string>(_globalTags, StringComparer.OrdinalIgnoreCase);

            // Normalize sensitive keys
            _sensitiveTagKeys = new HashSet<string>(_sensitiveTagKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            // Final validation (includes global tags/limits)
            ValidateOrThrow(validateGlobalTagsAgainstLimits: true);

            // Materialize read-only views
            _globalTagsRo = new ReadOnlyDictionary<string, string>(_globalTags);
            _sensitiveTagKeysRo = new ReadOnlyCollection<string>(new List<string>(_sensitiveTagKeys));

            _frozen = true;
            return this;
        }

        /// <summary>
        /// Creates a deep, unfrozen copy for mutation.
        /// </summary>
        public DiagnosticsOptions Clone()
        {
            var clone = new DiagnosticsOptions
            {
                DefaultComponent = _defaultComponent,
                MinimumLevel = _minimumLevel,
                GlobalTags = new Dictionary<string, string>(_globalTags, StringComparer.OrdinalIgnoreCase),
#pragma warning disable CS0618
                InMemoryCapacity = _inMemoryCapacity,
#pragma warning restore CS0618
                UseHighResolutionTiming = _useHighResolutionTiming,
                CaptureExceptions = _captureExceptions,
                EnableTracing = _enableTracing,
                EnableMetrics = _enableMetrics,
                EnableLogging = _enableLogging,
                StrictMetrics = _strictMetrics,
                MaxTagsPerEvent = _maxTagsPerEvent,
                MaxTagValueLength = _maxTagValueLength,
                MaxTraceBuffer = _maxTraceBuffer,
                TraceSampleRate = _traceSampleRate,
                SensitiveTagKeys = new List<string>(_sensitiveTagKeys),
                DispatcherFullMode = this.DispatcherFullMode,
                RedactTraceMessage = _redactTraceMessage,
                RedactExceptionMessage = _redactExceptionMessage,
                RedactExceptionStack = _redactExceptionStack,
                StrictBootstrapOptions = this.StrictBootstrapOptions,
                ShutdownTimeoutMs = _shutdownTimeoutMs,
                DispatcherQueueCapacity = _dispatcherQueueCapacity,
                MaxExceptionStackFrames = _maxExceptionStackFrames
            };

            clone.StrictGlobalTagKeys = this.StrictGlobalTagKeys;
            clone.AllowedCustomGlobalTagKeys = this.AllowedCustomGlobalTagKeys?.ToArray() ?? Array.Empty<string>();
            clone.AllowedCustomMetricTagKeys = this.AllowedCustomMetricTagKeys?.ToArray() ?? Array.Empty<string>();
            return clone;
        }

        // ----- Helpers -----
        private void ThrowIfFrozen()
        {
            if (_frozen)
                throw new InvalidOperationException("DiagnosticsOptions is frozen and cannot be modified.");
        }
    }
}
