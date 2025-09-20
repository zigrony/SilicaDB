using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.DiagnosticsCore.Instrumentation;

namespace Silica.DiagnosticsCore.Tests
{
    /// <summary>
    /// Self-contained harness that exercises DiagnosticsCore (metrics, tracing, redaction,
    /// timing scopes, lock scopes, dispatcher health) without external project dependencies.
    /// Now also exercises FileTraceSink.
    /// </summary>
    public static class DiagnosticsCoreTestHarness
    {
        public static void Run()
        {
            Console.WriteLine("=== DiagnosticsCore Test Harness ===");

            // 1) Configure options for an end-to-end, in-process run
            var options = new DiagnosticsOptions
            {
                DefaultComponent = "Harness",
                MinimumLevel = "trace",
                EnableMetrics = true,
                EnableTracing = true,
                EnableLogging = true, // enables ConsoleTraceSink
                DispatcherQueueCapacity = 256,
                ConsoleSinkIncludeTags = true,
                ConsoleSinkIncludeException = true,
                ConsoleSinkDropPolicy = DiagnosticsOptions.ConsoleDropPolicy.BlockWithTimeout,
                MaxTraceBuffer = 256, // BoundedInMemoryTraceSink capacity
                StrictMetrics = true,
                RequireTraceRedaction = true,
                MaxTagValueLength = 64,
                MaxTagsPerEvent = 10,
                MaxTraceMessageLength = 200,
                MaxExceptionStackFrames = 16,
                MaxInnerExceptionDepth = 2,
                StrictGlobalTagKeys = false
            };

            // Global tags and sensitive keys
            options.GlobalTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { TagKeys.Component, "DiagnosticsCoreHarness" },
                { TagKeys.Region, "dev" }
            };
            options.SensitiveTagKeys = new[] { "api_key", "secret", "password" };

            // Allow a couple of custom global/metric tag keys used below
            options.AllowedCustomGlobalTagKeys = new[] { "custom_env", "build" };
            options.AllowedCustomMetricTagKeys = new[] { "custom_env", "phase" };

            // Redaction knobs
            options.RedactTraceMessage = false;
            options.RedactExceptionMessage = true;
            options.RedactExceptionStack = false;

            options.Freeze();

            // 2) Start DiagnosticsCore once
            DiagnosticsCoreBootstrap.Stop(throwOnErrors: false);
            var bootstrap = DiagnosticsCoreBootstrap.Start(options);

            var metrics = bootstrap.Metrics;
            var traces = bootstrap.Traces;
            var dispatcher = bootstrap.Dispatcher;
            var bounded = bootstrap.BoundedSink;

            Console.WriteLine("-- Started DiagnosticsCore --");
            PrintStatus();

            // --- NEW: FileTraceSink test wiring ---
            var tempFile = Path.Combine(@"c:\temp\", $"harness_traces_{Guid.NewGuid():N}.log");
            var fileSink = new FileTraceSink(
                filePath: tempFile,
                minLevel: "trace",
                metrics: metrics,
                dropPolicy: FileTraceSink.DropPolicy.BlockWithTimeout,
                queueCapacity: 64,
                shutdownDrainTimeoutMs: 500
            ).WithFormatting(includeTags: true, includeException: true);

            dispatcher.RegisterSink(fileSink);

            traces.Emit("Harness", "FileSinkTest", "info",
                new Dictionary<string, string> { { TagKeys.Operation, "file_sink_write" } },
                "First trace to FileTraceSink");

            traces.Emit("Harness", "FileSinkTest", "warn",
                new Dictionary<string, string> { { TagKeys.Operation, "file_sink_write" } },
                "Second trace to FileTraceSink with warning");

            Thread.Sleep(100);

            Console.WriteLine($"\n-- FileTraceSink output ({tempFile}) --");
            try
            {
                var lines = File.ReadAllLines(tempFile);
                foreach (var line in lines.Take(3))
                    Console.WriteLine(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not read file sink output: {ex.Message}");
            }
            // --- END NEW ---

            // 3) Register a few harness metrics
            var counterDef = new MetricDefinition(
                Name: "harness.test_counter",
                Type: MetricType.Counter,
                Description: "Harness counter for sanity checks",
                Unit: "entries",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>());

            var timingDef = new MetricDefinition(
                Name: "harness.timing_ms",
                Type: MetricType.Histogram,
                Description: "Duration of sample timed operations (ms)",
                Unit: "ms",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>());

            var lockDef = new MetricDefinition(
                Name: "harness.lock_ms",
                Type: MetricType.Histogram,
                Description: "Duration of sample lock hold (ms)",
                Unit: "ms",
                DefaultTags: Array.Empty<KeyValuePair<string, object>>());

            metrics.Register(counterDef);
            metrics.Register(timingDef);
            metrics.Register(lockDef);

            // 4) Emit a few metrics
            metrics.Increment("harness.test_counter", 1,
                new KeyValuePair<string, object>(TagKeys.Component, "Harness"),
                new KeyValuePair<string, object>("custom_env", "local"));

            metrics.Increment("harness.test_counter", 2,
                new KeyValuePair<string, object>(TagKeys.Component, "Harness"),
                new KeyValuePair<string, object>("phase", "warmup"));

            // 5) Emit traces
            var tagsInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { TagKeys.Operation, "startup" },
                { "custom_env", "local" },
                { "api_key", "ABCD-1234" }
            };
            traces.Emit("Harness", "Bootstrap", "info", tagsInfo, "Harness startup event with a sensitive tag");

            var tagsWarn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { TagKeys.Operation, "I/O" },
                { "build", "debug" }
            };
            traces.Emit("Harness", "DiskCheck", "warn", tagsWarn, "Simulated warning: low disk");

            try
            {
                ThrowSomething();
            }
            catch (Exception ex)
            {
                var tagsErr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { TagKeys.Operation, "exception_path" },
                    { "secret", "top-secret" }
                };
                traces.Emit("Harness", "CrashyOp", "error", tagsErr, "Simulated exception occurred", ex);
            }

            // 6) Timing scope
            using (var timing = Timing.Start(
                component: "Harness",
                operation: "TimedWork",
                onComplete: scope =>
                {
                    var ms = scope.Elapsed.TotalMilliseconds;
                    metrics.Record("harness.timing_ms", ms,
                        new KeyValuePair<string, object>(TagKeys.Component, scope.Component),
                        new KeyValuePair<string, object>(TagKeys.Operation, scope.Operation));
                }))
            {
                Thread.Sleep(80);
            }

            // 7) Lock scope
            using (var lscope = new LockScope(
                component: "Harness",
                lockName: "SampleLock",
                onComplete: scope =>
                {
                    var ms = scope.Elapsed.TotalMilliseconds;
                    metrics.Record("harness.lock_ms", ms,
                        new KeyValuePair<string, object>(TagKeys.Component, scope.Component),
                        new KeyValuePair<string, object>(TagKeys.Field, scope.LockName));
                }))
            {
                Thread.Sleep(35);
            }

            // 8) Let the dispatcher pump
            Thread.Sleep(100);

            // 9) Snapshot health and bounded buffer
            Console.WriteLine("\n-- Dispatcher health --");
            var health = DiagnosticsCoreBootstrap.GetHealth();
            Console.WriteLine($"is_started={health.IsStarted}, pumps={health.ActivePumps}, sinks={health.RegisteredSinks}, total_q_depth={health.TotalQueueDepth}, is_cancelling={health.IsCancelling}");
            foreach (var kv in health.QueueDepthBySinkType)
                Console.WriteLine($"queue_depth[{kv.Key}]={kv.Value}");

            Console.WriteLine("\n-- BoundedInMemoryTraceSink snapshot --");
            var kept = bounded.GetSnapshot();
            Console.WriteLine($"retained={kept.Count}, dropped_total={bounded.DroppedCount}");
            int shown = 0;
            foreach (var e in kept)
            {
                Console.WriteLine($"{e.Timestamp:O} [{e.Status}] {e.Component}/{e.Operation} :: {Trunc(e.Message, 120)}");
                if (++shown >= 5) break;
            }

            // 10) Tear down
            DiagnosticsCoreBootstrap.Stop(throwOnErrors: true);
            Console.WriteLine("\n=== DiagnosticsCore Test Harness Complete ===");
        }

        private static void PrintStatus()
        {
            var st = DiagnosticsCoreBootstrap.GetStatus();
            Console.WriteLine($"is_started={st.IsStarted}, fp={Trunc(st.Fingerprint, 16)}, policy={st.DispatcherPolicy}, metrics={st.MetricsEnabled}, tracing={st.TracingEnabled}");
            Console.WriteLine("global_tags:");
            foreach (var kv in st.GlobalTags)
                Console.WriteLine($"  {kv.Key}={kv.Value}");
        }

        private static void ThrowSomething()
        {
            try
            {
                _ = 1 / int.Parse("0");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Higher-level failure", ex);
            }
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
