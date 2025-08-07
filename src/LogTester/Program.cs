using System;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Logging;

namespace SilicaDB.LogTester
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Filter Test (Info and above) ===");
            await RunFilterTestAsync();

            Console.WriteLine("\n=== Concurrency Test (Trace and above) ===");
            await RunConcurrencyTestAsync();

            Console.WriteLine("\n=== Shutdown Timeout Test ===");
            await RunShutdownTimeoutTestAsync();

            Console.WriteLine("\n=== Post-Dispose Logging Exception Test ===");
            await RunDisposeExceptionTestAsync();

            Console.WriteLine("\nAll tests completed.");
        }

        private static async Task RunFilterTestAsync()
        {
            // Only Information, Warning, Error, Critical get logged
            await using var logger = new SilicaLogger(
                filePath: "filter_test.txt",
                minLevel: LogLevel.Information,
                toConsole: true
            );

            logger.Log(LogLevel.Debug, "This DEBUG should NOT appear.");
            logger.Log(LogLevel.Information, "This INFORMATION should appear.");
            logger.Log(LogLevel.Warning, "This WARNING should appear.");
            logger.Log(LogLevel.Error, "This ERROR should appear.");
            logger.Log(LogLevel.Critical, "This CRITICAL should appear.");

            // Give the background writer a moment to flush
            await Task.Delay(100);
        }

        private static async Task RunConcurrencyTestAsync()
        {
            // Capture everything
            await using var logger = new SilicaLogger(
                filePath: "concurrency_test.txt",
                minLevel: LogLevel.Trace,
                toConsole: true
            );

            const int taskCount = 10;
            const int messagesPerTask = 20;
            var tasks = new Task[taskCount];
            var rng = new Random();

            for (int i = 0; i < taskCount; i++)
            {
                int taskId = i;
                tasks[i] = Task.Run(async () =>
                {
                    for (int j = 0; j < messagesPerTask; j++)
                    {
                        logger.Log(
                            LogLevel.Trace,
                            "Task {0} – message #{1}",
                            taskId,
                            j
                        );

                        // *** Random jitter to mix up enqueue order ***
                        await Task.Delay(rng.Next(1, 10));
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Final marker
            logger.Log(
                LogLevel.Information,
                "Completed {0} tasks of {1} messages",
                taskCount,
                messagesPerTask
            );

            // Give time to flush
            await Task.Delay(200);
        }

        private static async Task RunShutdownTimeoutTestAsync()
        {
            const string path = "shutdown_timeout_test.txt";
            await using var logger = new SilicaLogger(
                filePath: path,
                minLevel: LogLevel.Trace,
                toConsole: true
            );

            // Flood the logger
            for (int i = 0; i < 100; i++)
                logger.Log(LogLevel.Debug, "ShutdownTest – msg #{0}", i);

            // Bound shutdown to 50ms
            using var cts = new CancellationTokenSource(50);

            try
            {
                await logger.ShutdownAsync(cts.Token);
                Console.WriteLine("Logger shutdown completed within timeout.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Logger shutdown timed out.");
            }
        }

        private static async Task RunDisposeExceptionTestAsync()
        {
            await using var logger = new SilicaLogger(
                filePath: "dispose_exception_test.txt",
                minLevel: LogLevel.Trace,
                toConsole: true
            );

            logger.Log(LogLevel.Information, "Logging before disposal works.");

            // Dispose asynchronously
            await logger.DisposeAsync();

            try
            {
                // Should throw ObjectDisposedException
                logger.Log(LogLevel.Warning, "This should throw after disposal.");
                Console.WriteLine("ERROR: No exception thrown!");
            }
            catch (ObjectDisposedException ex)
            {
                Console.WriteLine($"Caught expected exception: {ex.Message}");
            }
        }
    }
}
