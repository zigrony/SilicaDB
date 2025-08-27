using System;
using System.Collections.Generic;
using System.IO;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;

namespace Silica.DiagnosticsCore.Tests
{
    /// <summary>
    /// Simple harness to verify Metrics + Tracing functionality from a console app.
    /// </summary>
    public static class DiagnosticsCoreHarness
    {
        public static void Run()
        {
            Console.WriteLine("=== DiagnosticsCore Test Harness ===");

            // --- Metrics setup ---
            var metricsManager = new InMemoryMetricsManager();
            var metricDef = new MetricDefinition(
                Name: "test_counter",
                Type: MetricType.Counter,
                Description: "A sample counter metric",
                Unit: "count",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>()
            );

            metricsManager.Register(metricDef);

            // Emit some metrics
            metricsManager.Increment("test_counter", 1,
                new KeyValuePair<string, object>(TagKeys.Component, "Harness"),
                new KeyValuePair<string, object>("custom_tag", "alpha"));

            metricsManager.Increment("test_counter", 2,
                new KeyValuePair<string, object>(TagKeys.Component, "Harness"),
                new KeyValuePair<string, object>("custom_tag", "beta"));

            // Dump metrics
            Console.WriteLine("\n-- Metrics Snapshot --");
            foreach (var record in metricsManager.Records)
            {
                // Build tags string without LINQ
                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var t in record.Tags)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(t.Key).Append('=').Append(t.Value);
                    first = false;
                }
                Console.WriteLine(
                    record.Definition.Name + " [" + sb.ToString() + "] = " + record.Value
                );
            }

            // --- Tracing setup ---
            var traceSink = new InMemoryTraceSink();

            // NEW: FileTraceSink setup
            string tempFilePath;
            try
            {
                tempFilePath = Path.Combine("c:\\temp\\", "Silica.log");
            }
            catch { tempFilePath = "Silica.log"; }

            var fileSink = new FileTraceSink(
                filePath: tempFilePath,
                minLevel: "trace",
                metrics: metricsManager,
                dropPolicy: FileTraceSink.DropPolicy.DropNewest,
                queueCapacity: 128
            ).WithFormatting(includeTags: true, includeException: true);

            var dispatcher = new TraceDispatcher();
            dispatcher.RegisterSink(traceSink);
            dispatcher.RegisterSink(fileSink);

            var traceManager = new TraceManager(dispatcher);

            // Emit some traces
            traceManager.Emit(
                component: "Harness",
                operation: "Op1",
                status: "info",
                message: "This is a test trace event"
            );

            traceManager.Emit(
                component: "Harness",
                operation: "Op2",
                status: "warn",
                message: "Something happened"
            );

            // Give dispatcher a moment to pump events (in case of async sinks)
            dispatcher.Dispose(); // ensures flush before reading
            fileSink.Dispose();    // flush file sink

            // Dump traces from in-memory sink
            Console.WriteLine("\n-- Trace Snapshot (InMemory) --");
            foreach (var trace in traceSink.Events)
            {
                Console.WriteLine(
                    $"[{trace.Timestamp:u}] {trace.Status} {trace.Component}: {trace.Message}"
                );
            }

            // Dump file sink output
            Console.WriteLine("\n-- Trace Snapshot (FileTraceSink) --");
            if (File.Exists(tempFilePath))
            {
                foreach (var line in File.ReadAllLines(tempFilePath))
                {
                    Console.WriteLine(line);
                }
            }
            else
            {
                Console.WriteLine("No file output found.");
            }

            Console.WriteLine("\n=== Harness Complete ===");
        }
    }
}
