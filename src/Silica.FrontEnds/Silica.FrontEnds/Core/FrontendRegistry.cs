// File: Silica.FrontEnds/Core/FrontendRegistry.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using System.Collections.ObjectModel;
using Silica.FrontEnds.Abstractions;
using Silica.FrontEnds.Diagnostics;
using Silica.FrontEnds.Exceptions;
using Silica.FrontEnds.Metrics;
using Silica.FrontEnds.Internal;
using Silica.DiagnosticsCore.Metrics;
using Microsoft.Extensions.Hosting;

namespace Silica.FrontEnds.Core
{
    /// <summary>
    /// Collects and configures frontends at startup.
    /// Contract-first: no implicit discovery; explicit registration only.
    /// </summary>
    public sealed class FrontendRegistry
    {
        private readonly List<IFrontend> _frontends = new List<IFrontend>();
        private readonly List<Exception> _failures = new List<Exception>();
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;

        public FrontendRegistry(IMetricsManager? metrics = null, string componentName = "Silica.FrontEnds")
        {
            // Ensure Frontend exception definitions are registered once early.
            try { Silica.FrontEnds.Exceptions.FrontendExceptions.RegisterAll(); } catch { }
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.FrontEnds" : componentName;
            if (_metrics is not null)
            {
                // Register the active gauge once, bound to FrontendState.
                try
                {
                    var gauge = FrontendMetrics.ActiveGauge with
                    {
                        LongCallback = () =>
                        {
                            long v = 0;
                            try { v = FrontendState.GetActiveCount(); } catch { }
                            return new[] { new System.Diagnostics.Metrics.Measurement<long>(v, new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.Component, _componentName)) };
                        }
                    };
                    // Attempt registration; ignore duplicates.
                    try { _metrics.Register(gauge); } catch { }
                }
                catch { }
            }
        }

        /// <summary>
        /// Returns a snapshot of failures encountered during the last ConfigureAll call.
        /// </summary>
        public IReadOnlyList<Exception> Failures
        {
            get { return new ReadOnlyCollection<Exception>(_failures); }
        }

        public void Add(IFrontend frontend)
        {
            if (frontend == null) throw new ArgumentNullException(nameof(frontend));
            string n = frontend.Name ?? string.Empty;
            if (n.Length == 0) throw new ArgumentException("Frontend.Name must be non-empty.", nameof(frontend));
            if (_names.Contains(n)) throw new ArgumentException("Duplicate frontend name: " + n, nameof(frontend));
            _frontends.Add(frontend);
            _names.Add(n);
        }

        /// <summary>
        /// Configure all registered frontends (builder phase), then build and configure the app (app phase).
        /// If any frontend intends to own host lifecycle, the caller should run the returned app (or registry can run).
        /// </summary>
        public WebApplication ConfigureAll(WebApplicationBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            _failures.Clear();
            int count = _frontends.Count;
            for (int i = 0; i < count; i++)
            {
                var f = _frontends[i];
                try
                {
                    FrontendDiagnostics.EmitDebug(
                        component: _componentName,
                        operation: "Registry.Configure",
                        message: "configuring_frontend",
                        ex: null,
                        more: new Dictionary<string, string> { ["frontend"] = f.Name },
                        allowDebug: true);

                    f.ConfigureBuilder(builder);

                    FrontendDiagnostics.Emit(
                        component: _componentName,
                        operation: "Registry.Configure",
                        level: "info",
                        message: $"Frontend '{f.Name}' configured.",
                        ex: null,
                        more: new Dictionary<string, string> { ["frontend"] = f.Name });

                }
                catch (Exception ex)
                {
                    FrontendDiagnostics.Emit(
                        component: _componentName,
                        operation: "Registry.Configure",
                        level: "error",
                        message: $"Frontend '{f.Name}' configuration failed.",
                        ex: ex);
                    // keep process alive; allow other frontends to continue
                    // but stamp a structured exception for upstream callers/tests that may aggregate failures.
                    var structured = new FrontendConfigurationFailedException(f.Name, ex);
                    FrontendDiagnostics.Emit(
                        component: _componentName,
                        operation: "Registry.Configure",
                        level: "error",
                        message: "frontend_configuration_failed_structured",
                        ex: structured);
                    _failures.Add(structured);
                }
            }

            // Build once
            var app = builder.Build();

            // App phase configuration (routes, lifetime)
            for (int i = 0; i < count; i++)
            {
                var f = _frontends[i];
                try
                {
                    f.ConfigureApp(app);
                    FrontendDiagnostics.Emit(_componentName, "Registry.ConfigureApp", "info", $"Frontend '{f.Name}' app configured.",
                        ex: null,
                        more: new Dictionary<string, string> { ["frontend"] = f.Name });
                }
                catch (Exception ex)
                {
                    var structured = new FrontendConfigurationFailedException(f.Name, ex);
                    FrontendDiagnostics.Emit(_componentName, "Registry.ConfigureApp", "error", "frontend_app_configuration_failed", structured);
                    _failures.Add(structured);
                }
            }

            return app;
        }

        /// <summary>
        /// Runs the built app if any registered frontend owns host lifecycle.
        /// Returns true if run was invoked; false otherwise.
        /// </summary>
        public bool RunIfAnyFrontendOwnsLifecycle(WebApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            bool shouldRun = false;
            int count = _frontends.Count;
            for (int i = 0; i < count; i++)
            {
                var f = _frontends[i];
                var fl = f as Silica.FrontEnds.Abstractions.IFrontendLifecycle;
                if (fl != null && fl.OwnsHostLifecycle)
                {
                    shouldRun = true;
                    break;
                }
            }

            if (shouldRun)
            {
                try
                {
                    app.Run();
                    return true;
                }
                catch (Exception ex)
                {
                    FrontendDiagnostics.Emit(_componentName, "Registry.Run", "error", "frontend_app_run_failed", ex);
                    // Surface structured failure for upstream parity when host run fails.
                    // Prefer FrontendConfigurationFailed for lack of specific endpoint context here.
                    var structured = new FrontendConfigurationFailedException("HostLifecycle", ex);
                    try
                    {
                        _failures.Add(structured);
                        FrontendDiagnostics.Emit(_componentName, "Registry.Run", "error", "frontend_app_run_failed_structured", structured);
                    }
                    catch { }
                    throw structured;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs the built app asynchronously if any registered frontend owns host lifecycle.
        /// Returns true if run was invoked; false otherwise. Honors the provided CancellationToken.
        /// </summary>
        public async Task<bool> RunIfAnyFrontendOwnsLifecycleAsync(WebApplication app, CancellationToken cancellationToken)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            bool shouldRun = false;
            int count = _frontends.Count;
            for (int i = 0; i < count; i++)
            {
                var f = _frontends[i];
                var fl = f as Silica.FrontEnds.Abstractions.IFrontendLifecycle;
                if (fl != null && fl.OwnsHostLifecycle)
                {
                    shouldRun = true;
                    break;
                }
            }

            if (shouldRun)
            {
                try
                {
                    await app.RunAsync(cancellationToken);
                    // Normal completion (no cancellation): treat as successful run.
                    FrontendDiagnostics.Emit(_componentName, "Registry.RunAsync", "info", "frontend_app_run_completed");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is an expected control path; do not record as failure.
                    FrontendDiagnostics.Emit(_componentName, "Registry.RunAsync", "info", "frontend_app_run_cancelled");
                    return true;
                }
                catch (Exception ex)
                {
                    FrontendDiagnostics.Emit(_componentName, "Registry.RunAsync", "error", "frontend_app_run_failed", ex);
                    var structured = new FrontendConfigurationFailedException("HostLifecycle", ex);
                    try
                    {
                        _failures.Add(structured);
                        FrontendDiagnostics.Emit(_componentName, "Registry.RunAsync", "error", "frontend_app_run_failed_structured", structured);
                    }
                    catch { }
                    throw structured;
                }
            }
            return false;
        }
    }
}
