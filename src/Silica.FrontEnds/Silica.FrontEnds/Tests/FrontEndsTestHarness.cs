// File: Silica.FrontEnds.Tests/FrontEndsTestHarness.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Certificates.Metrics;
using Silica.FrontEnds.Core;
using Silica.FrontEnds.Kestrel;
using Silica.FrontEnds.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Silica.FrontEnds.Exceptions;

namespace Silica.FrontEnds.Tests
{
    /// <summary>
    /// Simple harness to validate FrontEnds plumbing and the default Kestrel frontend.
    /// Mirrors the minimal structure used across other Silica.* test harnesses.
    /// </summary>
    public static class FrontEndsTestHarness
    {
        /// <summary>
        /// Runs the harness. This will start (or reuse) DiagnosticsCore, register certificate metrics,
        /// create a FrontendRegistry, add a KestrelFrontend and configure it. KestrelFrontend
        /// currently owns the host lifecycle and will block until the host shuts down.
        /// </summary>
        public static void Run()
        {
            // Reuse existing DiagnosticsCore if already started by the caller; otherwise start it.
            DiagnosticsCoreBootstrap.BootstrapInstance instance;
            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                instance = DiagnosticsCoreBootstrap.Instance;
            }
            else
            {
                // 1) DiagnosticsCore bootstrap (minimal deterministic options for harness)
                var opts = new DiagnosticsOptions
                {
                    DefaultComponent = "FrontEndsTest",
                    MinimumLevel = "info",
                    EnableMetrics = true,
                    EnableTracing = true,
                    EnableLogging = true,
                    DispatcherQueueCapacity = 1024,
                    StrictMetrics = false,
                    RequireTraceRedaction = false,
                    MaxTagValueLength = 128,
                    MaxTagsPerEvent = 8,
                    SinkShutdownDrainTimeoutMs = 3000
                };
                opts.Freeze();
                instance = DiagnosticsCoreBootstrap.Start(opts);

                // Emit a bootstrap trace for parity with other harnesses
                var tags = new System.Collections.Generic.Dictionary<string, string>
                {
                    { TagKeys.Operation, "frontend_bootstrap" },
                    { "harness", "FrontEndsTest" }
                };
                instance.Traces.Emit("FrontEndsTest", "Bootstrap", "info", tags, "FrontEnds diagnostics started");
            }

            // 2) Register certificate metrics so Ephemeral provider reports into metrics manager in this process
            CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

            // 3) Build a WebApplicationBuilder for the frontend to consume
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            // 4) Create FrontendRegistry and register the default KestrelFrontend
            var registry = new FrontendRegistry(instance.Metrics, componentName: "Silica.FrontEnds");

            // Provide simple ephemeral options (short-lived cert)
            var kestrelOptions = new KestrelFrontendOptions
            {
                HttpsPort = 5001,
                HttpPort = 0,
                EphemeralOptions = new Silica.Certificates.Abstractions.CertificateProviderOptions
                {
                    LifetimeDays = 1.0,
                    IncludeMachineNameSan = false
                },
                EnableWindowsMachineKeySet = true,
                RootGetResponse = "FrontEndsTestHarness OK",
                OwnsHostLifecycle = true
                // Optional: demonstrate explicit bind address and protocol flags
                // BindAddress = "127.0.0.1",
                // EnableHttp2OnHttp = true
            };

            // Use the DiagnosticsCore metrics manager so FrontendMetrics flows into the same pipeline
            var kestrel = new KestrelFrontend(kestrelOptions, instance.Metrics, componentName: "Silica.FrontEnds.Kestrel.Test");
            registry.Add(kestrel);

            // 5) Configure all registered frontends, build once, configure app, then run according to options.
            try
            {
                FrontendDiagnostics.EmitDebug("Silica.FrontEnds.Tests", "Harness.Run", "configuring_frontends", null, null, allowDebug: true);
                var app = registry.ConfigureAll(builder);
                FrontendDiagnostics.Emit("Silica.FrontEnds.Tests", "Harness.Run", "info", "frontends_configured");

                // Delegate lifecycle decision to the registry using the lifecycle contract.
                bool ran = registry.RunIfAnyFrontendOwnsLifecycle(app);
                if (!ran)
                {
                    FrontendDiagnostics.EmitDebug("Silica.FrontEnds.Tests", "Harness.Run", "non_blocking_host", null, null, allowDebug: true);
                }
            }
            catch (Exception ex)
            {
                // Emit an error trace and rethrow to surface harness failure
                FrontendDiagnostics.Emit("Silica.FrontEnds.Tests", "Harness.Run", "error", "frontend_start_failed", ex);
                // Pass through structured exceptions as-is; wrap unstructured ones for parity.
                if (ex is FrontendBindFailedException || ex is FrontendConfigurationFailedException || ex is CertificateAcquireFailedException)
                {
                    throw;
                }
                else
                {
                    throw new FrontendConfigurationFailedException("UnknownFrontend", ex);
                }
            }
        }

        /// <summary>
        /// Async harness variant that honors an external CancellationToken.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            // Reuse or bootstrap DiagnosticsCore (same as Run).
            DiagnosticsCoreBootstrap.BootstrapInstance instance;
            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                instance = DiagnosticsCoreBootstrap.Instance;
            }
            else
            {
                var opts = new DiagnosticsOptions
                {
                    DefaultComponent = "FrontEndsTest",
                    MinimumLevel = "info",
                    EnableMetrics = true,
                    EnableTracing = true,
                    EnableLogging = true,
                    DispatcherQueueCapacity = 1024,
                    StrictMetrics = false,
                    RequireTraceRedaction = false,
                    MaxTagValueLength = 128,
                    MaxTagsPerEvent = 8,
                    SinkShutdownDrainTimeoutMs = 3000
                };
                opts.Freeze();
                instance = DiagnosticsCoreBootstrap.Start(opts);
                var tags = new System.Collections.Generic.Dictionary<string, string>
                {
                    { TagKeys.Operation, "frontend_bootstrap" },
                    { "harness", "FrontEndsTest" }
                };
                instance.Traces.Emit("FrontEndsTest", "Bootstrap", "info", tags, "FrontEnds diagnostics started");
            }

            CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            var registry = new FrontendRegistry(instance.Metrics, componentName: "Silica.FrontEnds");

            var kestrelOptions = new KestrelFrontendOptions
            {
                HttpsPort = 5001,
                HttpPort = 0,
                EphemeralOptions = new Silica.Certificates.Abstractions.CertificateProviderOptions
                {
                    LifetimeDays = 1.0,
                    IncludeMachineNameSan = false
                },
                EnableWindowsMachineKeySet = true,
                RootGetResponse = "FrontEndsTestHarness OK",
                OwnsHostLifecycle = true
            };

            var kestrel = new KestrelFrontend(kestrelOptions, instance.Metrics, componentName: "Silica.FrontEnds.Kestrel.Test");
            registry.Add(kestrel);

            try
            {
                FrontendDiagnostics.EmitDebug("Silica.FrontEnds.Tests", "Harness.RunAsync", "configuring_frontends", null, null, allowDebug: true);
                var app = registry.ConfigureAll(builder);
                FrontendDiagnostics.Emit("Silica.FrontEnds.Tests", "Harness.RunAsync", "info", "frontends_configured");

                bool ran = await registry.RunIfAnyFrontendOwnsLifecycleAsync(app, cancellationToken);
                if (!ran)
                {
                    FrontendDiagnostics.EmitDebug("Silica.FrontEnds.Tests", "Harness.RunAsync", "non_blocking_host", null, null, allowDebug: true);
                }
            }
            catch (Exception ex)
            {
                FrontendDiagnostics.Emit("Silica.FrontEnds.Tests", "Harness.RunAsync", "error", "frontend_start_failed", ex);
                if (ex is FrontendBindFailedException || ex is FrontendConfigurationFailedException || ex is CertificateAcquireFailedException)
                {
                    throw;
                }
                else
                {
                    throw new FrontendConfigurationFailedException("UnknownFrontend", ex);
                }
            }
        }
    }
}
