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
using Silica.Storage.Encryption.Testing;

class Program
{
    static async Task Main(string[] args)
    {

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

        // 4) Run your harnesses
        await EvictionsTestHarness.Run();
        await StorageTestHarness.RunAsync();
        await EncryptionTestHarness.RunAsync();

        Console.WriteLine("All test harnesses completed.");

        // 5) Stop DiagnosticsCore cleanly
        DiagnosticsCoreBootstrap.Stop(throwOnErrors: true);
    }
}
