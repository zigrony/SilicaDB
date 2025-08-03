using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Metrics;
using SilicaDB.Metrics.Interfaces;

namespace SilicaDB.MetricsRecorderTester
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== SilicaDB MetricsRecorder Tester ===\n");

            await RunSuite("Single-Threaded Basic Test", TestSingleThreaded);
            await RunSuite("Multi-Threaded Concurrency Test", TestMultiThreaded);
            await RunSuite("Flush-While-Producing Test", TestFlushWhileProducing);

            Console.WriteLine("\nAll tests completed successfully.  Press ENTER to exit.");
            Console.ReadLine();
        }

        static async Task RunSuite(string name, Func<Task> suite)
        {
            Console.Write($"--- {name,-40}");
            var sw = Stopwatch.StartNew();
            try
            {
                await suite();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {ex.Message} ({sw.ElapsedMilliseconds} ms)");
            }
            finally
            {
                Console.ResetColor();
            }
        }

        // 1) Basic single-threaded exercise
        static async Task TestSingleThreaded()
        {
            await using var metrics = new MetricsRecorder();
            metrics.IncrementCounterAsync("orders.created", 1);
            metrics.RecordHistogramAsync("orders.latency", 123.45);
            metrics.RecordGaugeAsync("current.load", 0.75);

            await metrics.FlushAsync();
            await metrics.DisposeAsync();
        }

        // 2) Multi-threaded stress
        static async Task TestMultiThreaded()
        {
            const int producerCount = 8;
            const int eventsPerProducer = 50_000;

            await using var metrics = new MetricsRecorder();
            var producers = new List<Task>();

            for (int p = 0; p < producerCount; p++)
            {
                producers.Add(Task.Run(() =>
                {
                    var rnd = new Random(p);
                    for (int i = 0; i < eventsPerProducer; i++)
                    {
                        metrics.IncrementCounterAsync("orders.created");
                        metrics.RecordHistogramAsync("orders.size", rnd.NextDouble() * 100);
                        metrics.RecordGaugeAsync("cache.hitratio", rnd.NextDouble());
                    }
                }));
            }

            await Task.WhenAll(producers);
            await metrics.FlushAsync();
            await metrics.DisposeAsync();
        }

        // 3) Flush while producer is still generating
        static async Task TestFlushWhileProducing()
        {
            var cts = new CancellationTokenSource();
            await using var metrics = new MetricsRecorder();

            try
            {
                var producer = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        metrics.IncrementCounterAsync("heartbeat", 1);
                        await Task.Delay(1, cts.Token);
                    }
                });

                await Task.Delay(200);
                await metrics.FlushAsync();

                cts.Cancel();

                try { await producer; }
                catch (TaskCanceledException) { /* expected, ignore */ }

                await metrics.FlushAsync();
            }
            finally
            {
                cts.Dispose();
            }

            await metrics.DisposeAsync();
        }
    }
}
