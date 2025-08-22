using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics; // for Measurement<>
using System.Linq;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.DiagnosticsCore.Tests; // InMemoryDiagnosticsHarness

namespace DiagnosticsCoreConsoleTest
{
    internal class Program
    {
        private const bool EnableStressLoop = true; // ← flip to false to disable stress mode
        private const int StressIterations = 1_000;

        private static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Banner("Silica.DiagnosticsCore Console Test");

            var diag = new InMemoryDiagnosticsHarness();

            Section("Recording metrics");

            // Counter
            var widgetsProcessed = new MetricDefinition(
                Name: "widgets_processed",
                Type: MetricType.Counter,
                Description: "Total number of widgets processed",
                Unit: "count",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>()
            );
            diag.Metrics.Register(widgetsProcessed);

            // Increments for different tag values
            diag.Metrics.Increment("widgets_processed", 1, new KeyValuePair<string, object>("region", "us-east"));
            diag.Metrics.Increment("widgets_processed", 5, new KeyValuePair<string, object>("region", "eu-west"));
            diag.Metrics.Increment("widgets_processed", 3, new KeyValuePair<string, object>("region", "ap-south"));

            // Histogram
            diag.Metrics.Record("processing_time_ms", 42.5, new KeyValuePair<string, object>("region", "us-east"));
            diag.Metrics.Record("processing_time_ms", 37.2, new KeyValuePair<string, object>("region", "eu-west"));

            // Observable gauge
            var queueDepthDef = new MetricDefinition(
                Name: "queue_depth",
                Type: MetricType.ObservableGauge,
                Description: "Depth of processing queue",
                Unit: "items",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                LongCallback: () => new[]
                {
                    new Measurement<long>(new Random().Next(0, 100), default)
                }
            );
            diag.Metrics.Register(queueDepthDef);

            // Prime gauge once for output
            var longs = queueDepthDef.LongCallback?.Invoke() ?? Array.Empty<Measurement<long>>();
            foreach (var m in longs)
            {
                diag.Metrics.Record(queueDepthDef.Name, m.Value);
            }

            Section("Recording trace events");

            // Success
            diag.Traces.Emit(new TraceEvent(
                timestamp: DateTimeOffset.UtcNow,
                component: "TestHarness",
                operation: "WidgetProcessed",
                status: "success",
                tags: new Dictionary<string, string>
                {
                    { "widget_id", "123" },
                    { "region", "us-east" }
                },
                message: "Widget processed successfully",
                exception: null,
                correlationId: Guid.NewGuid(),
                spanId: Guid.NewGuid()
            ));

            // Failure with exception
            try
            {
                ThrowWidgetProcessingError();
            }
            catch (Exception ex)
            {
                diag.Traces.Emit(new TraceEvent(
                    timestamp: DateTimeOffset.UtcNow,
                    component: "TestHarness",
                    operation: "WidgetProcessed",
                    status: "failure",
                    tags: new Dictionary<string, string>
                    {
                        { "widget_id", "999" },
                        { "region", "us-east" }
                    },
                    message: "Widget processing failed",
                    exception: ex,
                    correlationId: Guid.NewGuid(),
                    spanId: Guid.NewGuid()
                ));
            }

            if (EnableStressLoop)
            {
                Section($"Stress loop: {StressIterations:N0} iterations");
                var rand = new Random();
                for (int i = 0; i < StressIterations; i++)
                {
                    // Alternate tag values to push cardinality
                    var region = (i % 3) switch
                    {
                        0 => "us-east",
                        1 => "eu-west",
                        _ => "ap-south"
                    };
                    diag.Metrics.Increment("widgets_processed", 1,
                        new KeyValuePair<string, object>("region", region));
                    diag.Metrics.Record("processing_time_ms", rand.NextDouble() * 100,
                        new KeyValuePair<string, object>("region", region));

                    // Occasional failure traces
                    if (i % 10 == 0)
                    {
                        diag.Traces.Emit(new TraceEvent(
                            timestamp: DateTimeOffset.UtcNow,
                            component: "StressHarness",
                            operation: "WidgetProcessed",
                            status: "failure",
                            tags: new Dictionary<string, string>
                            {
                                { "widget_id", i.ToString() },
                                { "region", region }
                            },
                            message: "Widget processing failed in stress loop",
                            exception: new InvalidOperationException("Stress loop exception"),
                            correlationId: Guid.NewGuid(),
                            spanId: Guid.NewGuid()
                        ));
                    }
                    else
                    {
                        diag.Traces.Emit(new TraceEvent(
                            timestamp: DateTimeOffset.UtcNow,
                            component: "StressHarness",
                            operation: "WidgetProcessed",
                            status: "success",
                            tags: new Dictionary<string, string>
                            {
                                { "widget_id", i.ToString() },
                                { "region", region }
                            },
                            message: "Widget processed in stress loop",
                            exception: null,
                            correlationId: Guid.NewGuid(),
                            spanId: Guid.NewGuid()
                        ));
                    }
                }
            }

            Section("Captured Metrics");
            PrintMetrics(diag);

            Section("Captured Trace Events");
            PrintTraces(diag);

            Banner("Test Complete");
        }

        private static void Banner(string text)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(new string('═', text.Length + 8));
            Console.WriteLine($"   {text}   ");
            Console.WriteLine(new string('═', text.Length + 8));
            Console.ResetColor();
        }

        private static void Section(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n--- {text} ---");
            Console.ResetColor();
        }

        private static void PrintMetrics(InMemoryDiagnosticsHarness diag)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            foreach (var record in diag.Metrics.Records)
            {
                Console.WriteLine($"• {record.Definition.Name,-25} {record.Value,8}");
                foreach (var tag in record.Tags)
                {
                    Console.WriteLine($"    ↳ {tag.Key} = {tag.Value}");
                }
            }
            Console.ResetColor();
        }

        private static void PrintTraces(InMemoryDiagnosticsHarness diag)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            foreach (var evt in diag.Traces.Events)
            {
                Console.WriteLine($"• [{evt.Status.ToUpper()}] {evt.Component}/{evt.Operation} @ {evt.Timestamp:O}");
                foreach (var tag in evt.Tags)
                {
                    Console.WriteLine($"    ↳ {tag.Key} = {tag.Value}");
                }
                if (!string.IsNullOrEmpty(evt.Message))
                    Console.WriteLine($"    ✎ {evt.Message}");
                if (evt.Exception != null)
                    Console.WriteLine($"    ⚠ {evt.Exception.GetType().Name}: {evt.Exception.Message}");
            }
            Console.ResetColor();
        }

        private static void ThrowWidgetProcessingError()
        {
            throw new InvalidOperationException("Simulated processing exception");
        }
    }
}
