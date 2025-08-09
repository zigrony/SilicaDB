using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using SilicaDB.TestRunner.Suites;

namespace SilicaDB.TestRunner
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // 1) Register your suites here
            ITestSuite[] allSuites =
            {
        new BufferPoolTestSuite(),
        new CheckpointTestSuite(),
        //new DeviceTestSuite(),
        //new DurabilityTestSuite(),
        //new EvictionCacheTestSuite(),
        //new LogTestSuite(),
        //new MetricsRecorderTestSuite(),
        //new RecoveryTestSuite(),
        //new WalTestSuite()
      };

            // 2) Pick based on args
            var toRun = ParseArgs(args, allSuites);

            // 3) Run & collect timings
            var results = new List<(string Name, bool Passed, TimeSpan Elapsed)>();
            foreach (var suite in toRun)
            {
                var sw = Stopwatch.StartNew();
                Console.WriteLine($"\n=== {suite.Name} ===");
                bool passed = true;
                try
                {
                    await suite.RunAsync(new TestContext(suite.Name));
                }
                catch (Exception ex)
                {
                    passed = false;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"❌ {suite.Name} failed: {ex.Message}");
                    Console.ResetColor();
                }
                sw.Stop();
                results.Add((suite.Name, passed, sw.Elapsed));
            }

            PrintSummary(results);
            return results.All(r => r.Passed) ? 0 : 1;
        }

        static IEnumerable<ITestSuite> ParseArgs(
          string[] args, ITestSuite[] suites)
        {
            if (args.Length == 0 || args.Contains("--all"))
                return suites;

            var idx = Array.IndexOf(args, "--suites");
            if (idx >= 0 && idx + 1 < args.Length)
            {
                var names = args[idx + 1]
                  .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return suites.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            }

            Console.WriteLine("Usage: TestRunner [--all] [--suites Name1,Name2]");
            Environment.Exit(0);
            return Array.Empty<ITestSuite>();
        }

        static void PrintSummary(List<(string Name, bool Passed, TimeSpan Elapsed)> results)
        {
            Console.WriteLine("\n=== SUMMARY ===");
            Console.WriteLine("| Suite                 | Status | Time(ms) |");
            Console.WriteLine("|-----------------------|--------|----------|");
            foreach (var (Name, Passed, Elapsed) in results)
            {
                var status = Passed ? "PASS" : "FAIL";
                Console.WriteLine($"| {Name,-21} | {status,-6} | {Elapsed.TotalMilliseconds,8:F0} |");
            }
        }
    }
}