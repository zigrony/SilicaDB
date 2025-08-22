// File: Silica.DiagnosticsCore/DiagnosticsCoreBootstrap.cs
using System;
using System.Threading;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Configures and wires up the diagnostics pipeline. Call Start(...) once.
    /// Idempotent: subsequent Start calls return the same instance.
    /// Stop() disposes resources and clears the singleton for a clean restart.
    /// </summary>
    public static class DiagnosticsCoreBootstrap
    {
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

        public static BootstrapInstance Start(DiagnosticsOptions options, IMetricsManager? innerMetrics = null)
        {
            if (options is null)
                throw new ArgumentNullException(nameof(options));

            ValidateOptions(options);
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

                // Choose inner metrics manager (pass validator explicitly, no reflection)
                effectiveInner =
                        options.EnableMetrics
                            ? (innerMetrics ?? new MetricsManager(registry, meterName: "Silica.DiagnosticsCore"))
                            : new NoOpMetricsManager();

                var ownManager = options.EnableMetrics && innerMetrics is null;


                MetricsFacade? metricsRef = null;
                Action<string>? onDrop = null;
                if (options.EnableMetrics)
                {
                    onDrop = name => metricsRef?.Increment(
                        DiagCoreMetrics.MetricsDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, "unknown_metric"));
                }

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

                metricsRef = metrics;

                if (options.EnableMetrics)
                {
                    metrics.Register(DiagCoreMetrics.MetricsDropped);
                    metrics.Register(DiagCoreMetrics.TracesDropped);
                    metrics.Register(DiagCoreMetrics.TracesRedacted);
                    metrics.Register(DiagCoreMetrics.TraceTagsDropped);
                    metrics.Register(DiagCoreMetrics.TraceTagsTruncated);
                    metrics.Register(DiagCoreMetrics.BootstrapOptionsConflict);
                    metrics.Register(DiagCoreMetrics.MetricTagsTruncated);
                    metrics.Register(DiagCoreMetrics.MetricTagsRejected);
                    metrics.Register(DiagCoreMetrics.IgnoredConfigFieldSet);
                    metrics.Register(DiagCoreMetrics.MetricsLenientNoAutoreg);
                    // Now that metrics are registered and available, wire tag drop accounting
                    tagValidator.SetCallbacks(
                        onTagRejectedCount: count => { if (count > 0) metrics.Increment(DiagCoreMetrics.MetricTagsRejected.Name, count); },
                        onTagTruncatedCount: count => { if (count > 0) metrics.Increment(DiagCoreMetrics.MetricTagsTruncated.Name, count); });

                }

                // Surface ignored knobs (only those not enforced by the pipeline).
                // Keep this list tight; don't flag knobs that are actually enforced.
                if (options.EnableMetrics)
                {
                    // If the caller supplied an external metrics manager, skip per-instance observable gauges
                    // to avoid re-registration conflicts across restarts; surface this as an ignored field.
                    if (!ownManager)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field,
                                "infra_gauges_disabled_external_metrics_manager"));

                    if (options.UseHighResolutionTiming != DiagnosticsOptions.Defaults.UseHighResolutionTiming)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "UseHighResolutionTiming"));
                    if (options.CaptureExceptions != DiagnosticsOptions.Defaults.CaptureExceptions)
                        metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "CaptureExceptions"));

                    // MaxExceptionStackFrames is enforced by DefaultTraceRedactor; do not flag as ignored.
                    //if (options.MaxExceptionStackFrames != DiagnosticsOptions.Defaults.MaxExceptionStackFrames)
                    //    metrics.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                    //        new KeyValuePair<string, object>(TagKeys.Field, "MaxExceptionStackFrames"));

                }

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
                    traceTagValidator.SetCallbacks(
                        onTagRejectedCount: c => { if (c > 0) metrics.Increment(DiagCoreMetrics.TraceTagsDropped.Name, c); },
                        onTagTruncatedCount: c => { if (c > 0) metrics.Increment(DiagCoreMetrics.TraceTagsTruncated.Name, c); });
                traceTagValidator.AddAllowedKeys(options.GlobalTags?.Keys ?? Array.Empty<string>());
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

                if (options.EnableLogging)
                {
                    try
                    {
                        dispatcher.RegisterSink(new LoggingTraceSink(options.MinimumLevel), options.DispatcherFullMode);
                    }
                    catch (Exception)
                    {
                        // Surface sink init failure
                        metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.DropCause, "sink_init_failed"),
                            new KeyValuePair<string, object>(TagKeys.Sink, "LoggingTraceSink"));
                    }

                }

                // 4) In-memory, bounded trace sink
                var bounded = new BoundedInMemoryTraceSink(
                    options.MaxTraceBuffer,
                    metrics,
                    registerGauges: registerInfraGauges && options.EnableMetrics);

                dispatcher.RegisterSink(bounded, options.DispatcherFullMode);

                // 5) Capture singleton
                _current = new BootstrapInstance(
                    Metrics: metrics,
                    Traces: traceMgr,
                    Dispatcher: dispatcher,
                    BoundedSink: bounded,
                    Options: options,
                    StartedAt: DateTimeOffset.UtcNow);

                // One-time startup signal with the active options fingerprint
                try
                {
                    var fp = options.ComputeFingerprint();
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { TagKeys.Policy, options.DispatcherFullMode.ToString().ToLowerInvariant() },
                        { TagKeys.Field, "options_fp" }
                    };
                    traceMgr.Emit(TraceCategory.Observability, "bootstrap", "info", tags, fp);
                }
                catch { /* never fail startup on diagnostics emission */ }
                return _current;
            }
        }


        public static void Stop()
        {
            lock (_gate) // serialize with Start()
            {
                var instance = _current;
                if (instance is null) return;
                _current = null;

                List<Exception>? disposeErrors = null;

                void SafeDispose(object? maybeDisposable)
                {
                    if (maybeDisposable is IDisposable d)
                    {
                        try { d.Dispose(); }
                        catch (Exception ex)
                        {
                            disposeErrors ??= new List<Exception>();
                            disposeErrors.Add(ex);
                        }
                    }
                }

                // 1. Stop dispatcher first to halt async inflight work
                SafeDispose(instance.Dispatcher);
                // 2. Clear and dispose bounded sink (free backlog for clean restart)
                SafeDispose(instance.BoundedSink);
                // 3. Dispose metrics (facade forwards to real MetricsManager)
                SafeDispose(instance.Metrics);

                if (disposeErrors is { Count: > 0 })
                    throw new AggregateException(
                        "One or more errors occurred during DiagnosticsCore shutdown.",
                        disposeErrors);
            }
        }


        private static void ValidateOptions(DiagnosticsOptions options)
        {
            if (options.MaxTagsPerEvent < 0)
                throw new ArgumentOutOfRangeException(nameof(options.MaxTagsPerEvent), "Must be >= 0.");

            if (options.MaxTagValueLength < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxTagValueLength), "Must be >= 1.");

            if (options.MaxTraceBuffer <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.MaxTraceBuffer), "Must be > 0.");

            if (options.DispatcherQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.DispatcherQueueCapacity), "Must be > 0.");

            // If present, ensure 0 <= TraceSampleRate <= 1 (null is allowed and means "use EffectiveTraceSampleRate")
            if (options.TraceSampleRate is double rate && (rate < 0.0 || rate > 1.0))
                throw new ArgumentOutOfRangeException(nameof(options.TraceSampleRate), "Must be between 0.0 and 1.0.");

            if (!Enum.IsDefined(typeof(System.Threading.Channels.BoundedChannelFullMode), options.DispatcherFullMode))
                throw new ArgumentOutOfRangeException(nameof(options.DispatcherFullMode), "Unknown dispatcher full mode.");
        }
    }
}
