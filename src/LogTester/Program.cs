using System;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Logging;
using SilicaDB.Metrics;

namespace SilicaDB.LogTester
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== SilicaLogger Test Suite ===");
            string logPath = "silica_test.log";

            using var metrics = new MetricsManager();

            var logger = new SilicaLogger(
                filePath: logPath,
                metrics: metrics,
                minLevel: LogLevel.Trace,
                toConsole: true
            );

            Console.WriteLine("Logger initialized. Emitting test messages...\n");

            logger.Log(LogLevel.Information, "Startup complete. System ready at {0}.", DateTime.UtcNow);
            logger.Log(LogLevel.Warning, "High memory usage detected: {0} MB", 928.6);
            logger.Log(LogLevel.Debug, "Debug mode enabled for subsystem: {0}", "CacheLayer");
            logger.Log(LogLevel.Error, "Unhandled exception from {0}: {1}", "DeviceManager", "DeviceNotFound");

            Console.WriteLine("Sleeping to simulate workload...\n");
            await Task.Delay(250);

            logger.Log(LogLevel.Information, "Background job started: JobId={0}", Guid.NewGuid());
            logger.Log(LogLevel.Critical, "CRITICAL: DB corruption detected at sector {0}", 45_312);
            logger.Log(LogLevel.Trace, "Trace event: {0}", "LockAcquired[Page:113]");

            Console.WriteLine("\nShutting down logger...\n");

            try
            {
                var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await logger.ShutdownAsync(shutdownCts.Token);
                Console.WriteLine("Logger shutdown completed.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Logger shutdown timed out.");
            }

            Console.WriteLine("\n=== Metrics Snapshot ===\n");
            foreach (var metric in metrics.Snapshot())
            {
                Console.WriteLine($"{metric.Name} = {metric.Value}");
            }

            Console.WriteLine("\nTest run finished.");
        }
    }
}
