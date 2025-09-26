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

        // Register metrics
        CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        // Register traces
        // After DiagnosticsCoreBootstrap.Start(options)
        CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        // No need for CertificateDiagnostics.Initialize(...)


        var builder = WebApplication.CreateBuilder(args);

        // 2.5) Enable verbose auth diagnostics so EmitDebug calls surface
        //Silica.Authentication.Diagnostics.SetVerbose(true);

        // 3.5) Create a dummy metrics manager you can pass into LocalAuthenticator (same style as Concurrency harness)
        IMetricsManager dummyMetrics = new DummyMetricsManager();

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

        // Use ephemeral cert
        var certProvider = new EphemeralCertificateProvider();
        var cert = certProvider.GetCertificate();

        // On Windows, re-import with MachineKeySet so SChannel can access the private key
        if (OperatingSystem.IsWindows())
        {
            cert = new X509Certificate2(
                cert.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(5001, listenOptions =>
            {
                listenOptions.UseHttps(cert);
            });
        });

        var app = builder.Build();
        app.MapGet("/", () => "Hello from SilicaDB over ephemeral TLS!");
        app.Run();
        Console.ReadKey();


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

        // Create a token that cancels on Ctrl+C or process exit
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // prevent immediate process kill
            cts.Cancel();
        };

        // Pass it into the harness
        await FrontEndsTestHarness.RunAsync(cts.Token);

        Console.WriteLine("All test harnesses completed.");

        // 5) Stop DiagnosticsCore cleanly
        DiagnosticsCoreBootstrap.Stop(throwOnErrors: true);
    }
}
