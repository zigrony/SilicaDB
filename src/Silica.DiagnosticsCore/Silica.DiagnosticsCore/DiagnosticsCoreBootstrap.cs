// File: Silica.DiagnosticsCore/DiagnosticsCoreBootstrap.cs
using System;
using System.Threading;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Diagnostics.Metrics;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Configures and wires up the diagnostics pipeline. Call Start(...) once.
    /// Idempotent: subsequent Start calls return the same instance.
    /// Stop() disposes resources and clears the singleton for a clean restart.
    /// </summary>
    public static class DiagnosticsCoreBootstrap
    {
       /// <summary>
       /// True if DiagnosticsCore has been started and has a current instance.
       /// </summary>
       public static bool IsStarted
       {
           get
           {
               lock (_gate)
                   return _current is not null;
           }
       }
        private static readonly object _gate = new();

        public sealed record BootstrapInstance(
            MetricsFacade Metrics,
            TraceManager Traces,
            TraceDispatcher Dispatcher,
            BoundedInMemoryTraceSink BoundedSink,
            DiagnosticsOptions Options,
            DateTimeOffset StartedAt);

        private static volatile BootstrapInstance? _current;

        public static BootstrapInstance Instance =>
            _current ?? throw new InvalidOperationException(
                "DiagnosticsCoreBootstrap.Start(...) must be called first.");

        public sealed record DiagnosticsCoreStatus(
    bool IsStarted,
    string Fingerprint,
    DateTimeOffset StartedAt,
    string DispatcherPolicy,
    bool MetricsEnabled,
    bool TracingEnabled,
    IReadOnlyDictionary<string, string> GlobalTags);

        public static DiagnosticsCoreStatus GetStatus()
        {
            lock (_gate)
            {
                var inst = _current;
                if (inst is null)
                {
                    return new DiagnosticsCoreStatus(
                        IsStarted: false,
                        Fingerprint: string.Empty,
                        StartedAt: default,
                        DispatcherPolicy: string.Empty,
                        MetricsEnabled: false,
                        TracingEnabled: false,
                        GlobalTags: new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase));
                }
                var policy = inst.Options.DispatcherFullMode == BoundedChannelFullMode.Wait
                    ? BoundedChannelFullMode.DropWrite.ToString().ToLowerInvariant()
                    : inst.Options.DispatcherFullMode.ToString().ToLowerInvariant();
                return new DiagnosticsCoreStatus(
                    IsStarted: true,
                    Fingerprint: inst.Options.ComputeFingerprint(),
                    StartedAt: inst.StartedAt,
                    DispatcherPolicy: policy,
                    MetricsEnabled: inst.Options.EnableMetrics,
                    TracingEnabled: inst.Options.EnableTracing,
                    GlobalTags: new Dictionary<string, string>(inst.Options.EffectiveGlobalTags, StringComparer.OrdinalIgnoreCase));
            }
        }

        public static BootstrapInstance Restart(DiagnosticsOptions options, IMetricsManager? innerMetrics = null)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            options.Freeze();
            lock (_gate)
            {
                if (_current is null) return Start(options, innerMetrics);
                var incoming = options.ComputeFingerprint();
                var existing = _current.Options.ComputeFingerprint();
                if (string.Equals(incoming, existing, StringComparison.Ordinal))
                    return _current;
                Stop(throwOnErrors: false);
                return Start(options, innerMetrics);
            }
        }


        /// <summary>
        /// Attempts to start DiagnosticsCore. Returns true if started by this call,
        /// false if already started or if startup failed.
        /// </summary>
        public static bool TryStart(
           DiagnosticsOptions options,
           out BootstrapInstance? instance,
           IMetricsManager? innerMetrics = null)
       {
           lock (_gate)
           {
               if (_current is not null)
               {
                   instance = _current;
                   return false;
               }
               try
               {
                   instance = Start(options, innerMetrics);
                   return true;
               }
               catch
               {
                   instance = null;
                   return false;
               }
           }
       }
        public static BootstrapInstance Start(DiagnosticsOptions options, IMetricsManager? innerMetrics = null)
        {
            if (options is null)
                throw new ArgumentNullException(nameof(options));

            options.Freeze();

            lock (_gate) // serialize with Stop()
            {
                if (_current is not null)
                {
                    var incoming = options.ComputeFingerprint();
                    var existing = _current.Options.ComputeFingerprint();
                    if (!string.Equals(incoming, existing, StringComparison.Ordinal))
                    {
                        if (options.StrictBootstrapOptions)
                            throw new InvalidOperationException(
                                $"DiagnosticsCore already started with different options. " +
                                $"existing={existing}, incoming={incoming}. Call Stop() before restarting.");
                        // lenient-but-visible mode:
                        _current.Metrics.Increment(DiagCoreMetrics.BootstrapOptionsConflict.Name);

                        try
                        {
                            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { TagKeys.Field, "options_conflict" }
                            };
                            _current.Traces.Emit(
                                TraceCategory.Observability,
                                "bootstrap",
                                "warn",
                                tags,
                                $"existing={existing}, incoming={incoming}");
                        }
                        catch
                        {
                            /* never fail on diagnostics emission */
                        }
                    }
                    return _current;
                }
                // 1) Tag schema governance
                var baseKeys = new[]
                {
                    TagKeys.Pool, TagKeys.Device, TagKeys.Operation, TagKeys.Component,
                    TagKeys.Tenant, TagKeys.Status, TagKeys.Exception, TagKeys.Shard,
                    TagKeys.Thread, TagKeys.Concurrency, TagKeys.DropCause,
                    TagKeys.Region, TagKeys.WidgetId, TagKeys.Policy,
                    TagKeys.Sink, TagKeys.Field
                };

                var registry = new MetricRegistry();
                
                // Prepare to close over the IMetricsManager instance
                IMetricsManager? effectiveInner = null;

                // Tag validator with drop accounting (safe: callback will run only after effectiveInner is assigned)
                var tagValidator = new TagValidator(
                                            baseKeys,
                                            options.MaxTagsPerEvent,
                                            options.MaxTagValueLength,
                                            onTagDropCount: null);

                tagValidator.AddAllowedKeys(options.GlobalTags?.Keys ?? Array.Empty<string>());
                tagValidator.AddAllowedKeys(options.AllowedCustomGlobalTagKeys ?? Array.Empty<string>());
                // Choose inner metrics manager (pass validator explicitly, no reflection)
                effectiveInner =
                        options.EnableMetrics
                            ? (innerMetrics ?? new MetricsManager(registry, meterName: "Silica.DiagnosticsCore"))
                            : new NoOpMetricsManager();

                var ownManager = options.EnableMetrics && innerMetrics is null;

                Action<string>? onDrop = null;
                // Facade now accounts precise drop causes; leave onDrop unset to avoid double-count.

                var metrics = new MetricsFacade(
                    options.EnableMetrics
                        ? effectiveInner
                        : new NoOpMetricsManager(),
                    registry,
                    tagValidator,
                    strict: options.EnableMetrics && options.StrictMetrics,
                    onDrop: onDrop,
                    globalTags: options.EffectiveGlobalTags,
                    ownsInner: options.EnableMetrics && ownManager);

                if (options.EnableMetrics)
                {
                    metrics.Register(DiagCoreMetrics.MetricsDropped);
                    metrics.Register(DiagCoreMetrics.TracesDropped);
                    metrics.Register(DiagCoreMetrics.TracesEmitted);
                    metrics.Register(DiagCoreMetrics.TracesRedacted);
                    metrics.Register(DiagCoreMetrics.TraceTagsDropped);
                    metrics.Register(DiagCoreMetrics.TraceTagsTruncated);
                    metrics.Register(DiagCoreMetrics.BootstrapOptionsConflict);
                    metrics.Register(DiagCoreMetrics.MetricTagsTruncated);
                    metrics.Register(DiagCoreMetrics.MetricTagsRejected);
                    metrics.Register(DiagCoreMetrics.IgnoredConfigFieldSet);
                    metrics.Register(DiagCoreMetrics.MetricsLenientNoAutoreg);
                    metrics.Register(DiagCoreMetrics.ShutdownDisposeErrors);
                    // Note: Tag drop/truncate callbacks are already wired in MetricsFacade.

                }

                // Surface ignored knobs (only those not enforced by the pipeline).
                // Keep this list tight; don't flag knobs that are actually enforced.
                var ignoredFieldsForTrace = new List<string>();
                if (options.EnableMetrics)
                {
                    // If the caller supplied an external metrics manager, skip per-instance observable gauges
                    // to avoid re-registration conflicts across restarts; surface this as an ignored field.
                    if (!ownManager)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field,
                                "infra_gauges_disabled_external_metrics_manager"));

                    if (!ownManager) ignoredFieldsForTrace.Add("infra_gauges_disabled_external_metrics_manager");

                    if (options.UseHighResolutionTiming != DiagnosticsOptions.Defaults.UseHighResolutionTiming)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "UseHighResolutionTiming"));

                    if (options.UseHighResolutionTiming != DiagnosticsOptions.Defaults.UseHighResolutionTiming)
                        ignoredFieldsForTrace.Add("UseHighResolutionTiming");

                    if (options.CaptureExceptions != DiagnosticsOptions.Defaults.CaptureExceptions)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "CaptureExceptions"));

                    if (options.CaptureExceptions != DiagnosticsOptions.Defaults.CaptureExceptions)
                        ignoredFieldsForTrace.Add("CaptureExceptions");

                }

                // Strictly reject unsupported full mode in strict bootstrap configurations.
                // In lenient mode we still coerce to DropWrite (and surface metrics/traces below).
                if (options.DispatcherFullMode == BoundedChannelFullMode.Wait && options.StrictBootstrapOptions)
                    throw new InvalidOperationException(
                        "DispatcherFullMode=Wait is not supported in DiagnosticsCore. " +
                        "Set DispatcherFullMode=DropWrite (default) or disable StrictBootstrapOptions for lenient coercion.");

                // 3) Tracing pipeline
                var registerInfraGauges = ownManager; // only register gauges if we own the meter
                var dispatcher = new TraceDispatcher(
                    metrics,
                    options.DispatcherQueueCapacity,
                    options.ShutdownTimeoutMs,
                    registerGauges: registerInfraGauges && options.EnableMetrics);

                var redactor = new DefaultTraceRedactor(
                    options.SensitiveTagKeysReadonly,
                    metrics,
                    redactMessage: options.RedactTraceMessage,
                    redactExceptionMessage: options.RedactExceptionMessage,
                    redactExceptionStack: options.RedactExceptionStack,
                    maxExceptionStackFrames: options.MaxExceptionStackFrames);

                var defaultComponent = string.IsNullOrWhiteSpace(options.DefaultComponent)
                    ? "unknown"
                    : options.DefaultComponent;

                var traceTagValidator = new TagValidator(
                    baseKeys,
                    options.MaxTagsPerEvent,
                    options.MaxTagValueLength,
                    onTagDropCount: null);
                if (options.EnableMetrics)
                {
                    traceTagValidator.SetCallbacks(
                        onTagRejectedCount: c => { if (c > 0) metrics.Increment(DiagCoreMetrics.TraceTagsDropped.Name, c); },
                        onTagTruncatedCount: c => { if (c > 0) metrics.Increment(DiagCoreMetrics.TraceTagsTruncated.Name, c); });
                }

                // Always allow currently-set global keys and any governed global keys
                traceTagValidator.AddAllowedKeys(options.GlobalTags?.Keys ?? Array.Empty<string>());
                traceTagValidator.AddAllowedKeys(options.AllowedCustomGlobalTagKeys ?? Array.Empty<string>());
                tagValidator.AddAllowedKeys(options.AllowedCustomMetricTagKeys ?? Array.Empty<string>());
                if (options.SensitiveTagKeysReadonly.Count > 0)
                    traceTagValidator.AddAllowedKeys(options.SensitiveTagKeysReadonly);

                var traceMgr = new TraceManager(
                                            dispatcher,
                                            redactor,
                                            defaultComponent: defaultComponent,
                                            metrics: metrics,
                                            enableTracing: options.EnableTracing,
                                            globalTags: options.EffectiveGlobalTags,
                                            sampleRate: options.EffectiveTraceSampleRate,
                                            traceTagValidator: traceTagValidator,
                                            minimumLevel: options.MinimumLevel);

                if (options.DispatcherFullMode == BoundedChannelFullMode.Wait)
                {
                    try
                    {
                        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { TagKeys.Field, "dispatcher_fullmode_wait" },
                            { TagKeys.Policy, "coerced_to_dropwrite" }
                        };
                        traceMgr.Emit(TraceCategory.Observability, "bootstrap", "warn", tags,
                            "DispatcherFullMode=Wait is not supported; coerced to DropWrite.");
                    }
                    catch { /* no-throw */ }
                }

                // Warn once when StrictMetrics with external manager: registration must go through the facade
                if (options.EnableMetrics && !ownManager && options.StrictMetrics)
                {
                    metrics.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "strict_metrics_external_manager"));
                    try
                    {
                        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { TagKeys.Field, "strict_metrics_external_manager" }
                        };
                        traceMgr.Emit(TraceCategory.Observability, "bootstrap", "warn", tags,
                            "StrictMetrics=true with external manager: register metrics via DiagnosticsCore facade to avoid drops.");
                    }
                    catch { /* never fail startup on diagnostics emission */ }
                }

                if (options.EnableLogging)
                {
                    try
                    {
                        
#if DEBUG
                        dispatcher.RegisterSink(new LoggingTraceSink(options.MinimumLevel), options.DispatcherFullMode);
#else
                        // In release, avoid registering potentially blocking listeners
                        metrics?.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "EnableLogging_may_block_trace_listeners"));
                        try
                        {
                            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { TagKeys.Field, "EnableLogging_may_block_trace_listeners" }
                            };
                            traceMgr.Emit(TraceCategory.Observability, "bootstrap", "warn", tags,
                                "LoggingTraceSink disabled in Release to avoid blocking.");
                        }
                        catch { /* never fail startup on diagnostics emission */ }

#endif
                    }
                    catch (Exception)
                    {
                        // Surface sink init failure
                        metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.SinkInitFailed),
                            new KeyValuePair<string, object>(TagKeys.Sink, "LoggingTraceSink"));
                    }

                }

                // 4) In-memory, bounded trace sink
                var bounded = new BoundedInMemoryTraceSink(
                    options.MaxTraceBuffer,
                    metrics,
                    registerGauges: registerInfraGauges && options.EnableMetrics);

                dispatcher.RegisterSink(bounded, options.DispatcherFullMode);
                // Emit a sink registration trace for operator visibility
                try
                {
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { TagKeys.Sink, nameof(BoundedInMemoryTraceSink) },
                        { TagKeys.Policy, (options.DispatcherFullMode == BoundedChannelFullMode.Wait
                            ? BoundedChannelFullMode.DropWrite
                            : options.DispatcherFullMode).ToString().ToLowerInvariant() }
                    };
                    traceMgr.Emit(TraceCategory.Observability, "dispatcher", "info", tags, "sink_registered");
                }
                catch { /* never fail startup on diagnostics emission */ }


                // Surface bounded buffer policy so operators know it's append-only
                try
                {
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { TagKeys.Sink, nameof(BoundedInMemoryTraceSink) },
                        { TagKeys.Policy, "append_only" }
                    };
                    traceMgr.Emit(
                        TraceCategory.Observability,
                        "bootstrap",
                        "info",
                        tags,
                        "bounded_buffer_policy");
                }
                catch { /* never fail startup on diagnostics emission */ }
                // Surface previously-detected ignored config fields as traces once tracing is ready
                try
                {
                    foreach (var f in ignoredFieldsForTrace)
                    {
                        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { TagKeys.Field, f }
                        };
                        traceMgr.Emit(TraceCategory.Observability, "bootstrap", "warn", tags, "ignored_config_field_set");
                    }
                }
                catch { /* never fail startup on diagnostics emission */ }


                // 5) Capture singleton
                var startedAt = DateTimeOffset.UtcNow;
                _current = new BootstrapInstance(
                    Metrics: metrics,
                    Traces: traceMgr,
                    Dispatcher: dispatcher,
                    BoundedSink: bounded,
                    Options: options,
                    StartedAt: startedAt);

                // One-time startup signal with the active options fingerprint
                try
                {
                    var fp = options.ComputeFingerprint();
                    var effectivePolicy = options.DispatcherFullMode == BoundedChannelFullMode.Wait
                        ? BoundedChannelFullMode.DropWrite.ToString().ToLowerInvariant()
                        : options.DispatcherFullMode.ToString().ToLowerInvariant();
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                     {
                         { TagKeys.Policy, effectivePolicy },
                         { TagKeys.Field, "options_fp" }
                     };
                    traceMgr.Emit(TraceCategory.Observability, "bootstrap", "info", tags, fp);
                }
                catch { /* never fail startup on diagnostics emission */ }

                // Register an observable gauge for start time (Unix seconds) for dashboards
                try
                {
                    var startSeconds = startedAt.ToUnixTimeSeconds();
                    var def = new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                        Name: "diagcore.bootstrap.started_at_unix_seconds",
                        Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                        Description: "UTC start time of DiagnosticsCore as Unix seconds",
                        Unit: "s",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        DoubleCallback: () => new[] { new Measurement<double>(startSeconds) });
                    metrics.Register(def);
                }
                catch { /* swallow exporter/registration errors */ }

                return _current;
            }
        }


        public static void Stop(bool throwOnErrors = true)
        {
            lock (_gate) // serialize with Start()
            {
                var instance = _current;
                if (instance is null) return;
                _current = null;

                List<Exception>? disposeErrors = null;

                void SafeDispose(string component, object? maybeDisposable)
                {
                    if (maybeDisposable is IDisposable d)
                    {
                        try { d.Dispose(); }
                        catch (Exception ex)
                        {
                            disposeErrors ??= new List<Exception>();
                            disposeErrors.Add(ex);
                            try
                            {
                                // metrics live until we dispose them last
                                instance.Metrics?.Increment(
                                    DiagCoreMetrics.ShutdownDisposeErrors.Name, 1,
                                    new KeyValuePair<string, object>(TagKeys.Component, component),
                                    new KeyValuePair<string, object>(TagKeys.Exception, ex.GetType().Name));
                            }
                            catch { /* never let shutdown accounting fail */ }
                            // Also emit a trace for per-component dispose failures
                            try
                            {
                                var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    { TagKeys.Component, component },
                                    { TagKeys.Exception, ex.GetType().Name },
                                    { TagKeys.Field, "dispose_failure" }
                                };
                                instance.Traces?.Emit(TraceCategory.Observability, "shutdown", "error", tags, ex.ToString());
                            }
                            catch { /* swallow during shutdown */ }
                        }
                    }
                }

                // 0. Best-effort: signal shutdown start (keeps as-is)
                try { instance.Metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 0); } catch { }

                // 1. Emit a shutdown trace before tearing down dispatcher
                if (instance.Traces is not null)
                {
                    try
                    {
                        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { TagKeys.Field, "shutdown" },
                            { TagKeys.Policy, instance.Options.DispatcherFullMode.ToString().ToLowerInvariant() }
                        };
                        instance.Traces.Emit(TraceCategory.Observability, "bootstrap", "info", tags, "shutdown_initiated");
                    }
                    catch { /* never fail shutdown on diagnostics emission */ }
                }
                // 2. Stop dispatcher first to halt async inflight work
                SafeDispose("TraceDispatcher", instance.Dispatcher);
                // 3. Clear and dispose bounded sink (free backlog for clean restart)
                SafeDispose("BoundedInMemoryTraceSink", instance.BoundedSink);
                // 4. Dispose metrics last
                SafeDispose("MetricsFacade", instance.Metrics);

                // 5. Throw to inform caller if any dispose errors
                try
                {
                    var status = disposeErrors is { Count: > 0 } ? "partial" : "clean";
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { TagKeys.Field, "shutdown_complete" },
                        { TagKeys.Policy, instance.Options.DispatcherFullMode.ToString().ToLowerInvariant() },
                        { TagKeys.Status, status }
                    };
                    instance.Traces?.Emit(TraceCategory.Observability, "bootstrap", "info", tags,
                        $"errors={(disposeErrors?.Count ?? 0)}");
                }
                catch { /* swallow */ }

                if (disposeErrors is { Count: > 0 } && throwOnErrors)
                    throw new AggregateException("One or more errors occurred during DiagnosticsCore shutdown.", disposeErrors);
            }
        }
    }
}
