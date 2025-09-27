using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions.Testing;
using Silica.DiagnosticsCore.Tests;
using Silica.Evictions.Tests;
using Silica.Storage.Tests;
using Silica.BufferPool.Tests;
using Silica.PageAccess.Tests;
using Silica.Authentication.Tests;
using Silica.Concurrency.Tests;
using Silica.Durability.Tests;
using Silica.Sql.Lexer.Tests;
using Silica.Certificates.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Silica.Certificates.Metrics;
using Silica.Certificates.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Silica.Sessions.Tests;
using Silica.FrontEnds.Tests;
using Silica.UI.Core;
using Silica.UI.Config;

public static class DiagnosticsRegistration
{
    public static void RegisterAll(IMetricsManager metrics)
    {
        // Buffer pool
        Silica.BufferPool.Metrics.BufferPoolMetrics.RegisterAll(metrics, "Silica.BufferPool");

        // Concurrency
        Silica.Concurrency.Metrics.ConcurrencyMetrics.RegisterAll(metrics, "Silica.Concurrency");

        // Durability (WAL, checkpoints, recovery)
        Silica.Durability.Metrics.WalMetrics.RegisterAll(
            metrics, "Silica.Durability.WalManager",
            latestLsnProvider: () => 0,          // TODO: wire to WAL manager
            inFlightOpsProvider: () => 0         // TODO: wire to WAL manager
        );
        Silica.Durability.Metrics.CheckpointMetrics.RegisterAll(metrics, "Silica.Durability.CheckpointManager");
        Silica.Durability.Metrics.RecoveryMetrics.RegisterAll(metrics, "Silica.Durability.RecoveryManager");

        // Evictions
        Silica.Evictions.Metrics.EvictionMetrics.RegisterAll(metrics, "Silica.Evictions");

        // Storage
        Silica.Storage.Metrics.StorageMetrics.RegisterAll(
            metrics, "Silica.Storage",
            frameLockCacheSizeProvider: null,    // TODO: supply from storage engine
            freeSpaceProvider: null,
            queueDepthProvider: null,
            inFlightProvider: null
        );

        // Page access
        Silica.PageAccess.Metrics.PageAccessMetrics.RegisterAll(metrics, "Silica.PageAccess");

        // SQL lexer
        Silica.Sql.Lexer.Metrics.LexerMetrics.RegisterAll(metrics, "Silica.Sql.Lexer");

        // Authentication
        Silica.Authentication.Metrics.AuthenticationMetrics.RegisterAll(metrics, "Silica.Authentication");

        // Certificates
        Silica.Certificates.Metrics.CertificateMetrics.RegisterAll(metrics, "Silica.Certificates");

        // Frontends
        Silica.FrontEnds.Metrics.FrontendMetrics.RegisterAll(metrics, "Silica.FrontEnds");

        // Sessions
        Silica.Sessions.Metrics.SessionMetrics.RegisterAll(metrics, "Silica.Sessions");

        // UI
        Silica.UI.Metrics.UiMetrics.RegisterAll(metrics, "Silica.UI");
    }
}

class Program
{
    static async Task Main(string[] args)
    {




        // Enable auth verbose via env (no assembly change required)
        Environment.SetEnvironmentVariable("SILICA_AUTHENTICATION_VERBOSE", "1");

        // 1) Build configuration
        var options = new DiagnosticsOptions
        {
            DefaultComponent = "TestApp",
            MinimumLevel = "info",           // capture everything
            EnableMetrics = true,             // enable metrics pipeline
            EnableTracing = true,             // enable tracing pipeline
            EnableLogging = true,             // auto‑wires ConsoleTraceSink (internal)
            DispatcherQueueCapacity = 1024,   // bounded dispatcher queue
            StrictMetrics = true,              // drop & count unknown metrics
            RequireTraceRedaction = true,      // enforce redaction before dispatch
            MaxTagValueLength = 64,
            MaxTagsPerEvent = 10,
            SinkShutdownDrainTimeoutMs = 5000, // drain timeout on stop

            GlobalTags = new Dictionary<string, string>
            {
                { TagKeys.Component, "TestApp" },
                { TagKeys.Region, "dev" }
            },

            SensitiveTagKeys = new[] { "secret", "api_key", "password" }
        };

        // Freeze before starting
        options.Freeze();

        // 2) Start DiagnosticsCore
        var instance = DiagnosticsCoreBootstrap.Start(options);

        DiagnosticsRegistration.RegisterAll(instance.Metrics);

        // Register metrics
        //CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        // Register traces
        // After DiagnosticsCoreBootstrap.Start(options)
        //CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        // No need for CertificateDiagnostics.Initialize(...)


        var builder = WebApplication.CreateBuilder(args);

        // 2.5) Enable verbose auth diagnostics so EmitDebug calls surface
        //Silica.Authentication.Diagnostics.SetVerbose(true);

        // 3) Register an extra FileTraceSink
        var fileSink = new FileTraceSink(
            filePath: @"c:\temp\testapp.traces.log",
            minLevel: "trace",
            metrics: instance.Metrics,
            dropPolicy: FileTraceSink.DropPolicy.BlockWithTimeout,
            queueCapacity: 512
        );

        instance.Dispatcher.RegisterSink(fileSink, instance.Options.DispatcherFullMode);

        // 4) Register and emit a custom metric
        var myMetric = new MetricDefinition(
            Name: "testapp.requests",
            Type: MetricType.Counter,
            Description: "Number of requests handled",
            Unit: "count",
            DefaultTags: Array.Empty<KeyValuePair<string, object>>() // required param
        );

        instance.Metrics.Register(myMetric);
        instance.Metrics.Increment("testapp.requests", 1,
            new KeyValuePair<string, object>(TagKeys.Operation, "startup"));

        // 5) Emit a trace
        var tags = new Dictionary<string, string>
        {
            { TagKeys.Operation, "startup" },
            { "custom_env", "local" }
        };

        instance.Traces.Emit("TestApp", "Bootstrap", "info", tags, "Application started");

        // 6) Print status
        var status = DiagnosticsCoreBootstrap.GetStatus();
        Console.WriteLine($"Diagnostics started: {status.IsStarted}, Policy: {status.DispatcherPolicy}");

        CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        var optionsUI = new SilicaOptions
        {
            AuthConfig = new AuthConfig { Provider = "Local" },
            SessionConfig = new SessionConfig { IdleTimeoutMinutes = 20 },
            FrontEndConfig = new FrontEndConfig { Url = "https://0.0.0.0:5001" }
        };

        await using var system = new SilicaSystem(optionsUI);
        await system.StartAsync();

        Console.WriteLine("Silica running. Press Enter to stop...");
        Console.ReadLine();

        await system.StopAsync();

        //// Use ephemeral cert
        //var certProvider = new EphemeralCertificateProvider();
        //var cert = certProvider.GetCertificate();

        //// On Windows, re-import with MachineKeySet so SChannel can access the private key
        //if (OperatingSystem.IsWindows())
        //{
        //    cert = new X509Certificate2(
        //        cert.Export(X509ContentType.Pfx),
        //        (string?)null,
        //        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        //}

        //builder.WebHost.ConfigureKestrel(options =>
        //{
        //    options.ListenAnyIP(5001, listenOptions =>
        //    {
        //        listenOptions.UseHttps(cert);
        //    });
        //});

        //var app = builder.Build();
        //app.MapGet("/", () => "Hello from SilicaDB over ephemeral TLS!");
        //app.Run();
        //Console.ReadKey();


        // 4) Run your harnesses
        //LexerTestHarness.Run();
        //await EvictionsTestHarness.Run();
        //await StorageTestHarness.RunAsync();
        //await BufferPoolTestHarness.Run();
        //await DurabilityTestHarness.Run();
        //await PageAccessTestHarness.Run();
        //await ConcurrencyTestHarness.Run();
        //await PageAccessIntegrationHarnessPhysical.RunAsync();
        //await AuthenticationTestHarnessLocal.Run();
        //await AuthenticationTestHarnessNtlm.Run();
        //await AuthenticationTestHarnessKerberos.Run();
        //await AuthenticationTestHarnessJwt.Run();
        //await AuthenticationTestHarnessCertificate.Run();
        //await SessionsTestHarness.Run();

        //// Create a token that cancels on Ctrl+C or process exit
        //using var cts = new CancellationTokenSource();
        //Console.CancelKeyPress += (sender, e) =>
        //{
        //    e.Cancel = true; // prevent immediate process kill
        //    cts.Cancel();
        //};

        //// Pass it into the harness
        //await FrontEndsTestHarness.RunAsync(cts.Token);

        //Console.WriteLine("All test harnesses completed.");

        // 5) Stop DiagnosticsCore cleanly
        DiagnosticsCoreBootstrap.Stop(throwOnErrors: true);
    }
}
