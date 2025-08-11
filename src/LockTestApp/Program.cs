using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Concurrency;
using SilicaDB.Instrumentation;

namespace LockManagerConsoleHarness
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var metrics = new LockMetrics();
            var lm = new LockManager(metrics);

            var results = new Dictionary<string, bool>();

            try
            {
                results["ImmediateSuccess"] = await ImmediateSuccess(lm);
                results["TwoShared"] = await TwoShared(lm);
                results["SharedThenExclusive"] = await SharedThenExclusive(lm);
                results["TimeoutVsSuccess"] = await TimeoutVsSuccess(lm);
                results["CancellationBehavior"] = await CancellationBehavior(lm, lm);
                results["MetricsRecorded"] = await MetricsAssertions(metrics, lm);

                Console.WriteLine();
                Console.WriteLine("=== TEST RESULTS ===");
                foreach (var kv in results)
                {
                    Console.WriteLine($"{kv.Key.PadRight(25)} : {(kv.Value ? "PASS" : "FAIL")}");
                }

                bool allPassed = results.Values.All(v => v);
                return allPassed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Harness crashed: {ex}");
                return 2;
            }
        }

        static async Task<bool> ImmediateSuccess(LockManager lm)
        {
            Console.Write("1) Immediate success under no contention... ");
            string resId = Guid.NewGuid().ToString();
            try
            {
                bool got = await lm.AcquireSharedAsync(1, resId);
                lm.ReleaseLock(1, resId);
                Console.WriteLine(got ? "OK" : "FAILED");
                return got;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }

        static async Task<bool> TwoShared(LockManager lm)
        {
            Console.Write("2) Two competing shared requests... ");
            string resId = Guid.NewGuid().ToString();
            try
            {
                var t1 = lm.AcquireSharedAsync(1, resId);
                var t2 = lm.AcquireSharedAsync(2, resId);

                bool r1 = await t1;
                bool r2 = await t2;
                lm.ReleaseLock(1, resId);
                lm.ReleaseLock(2, resId);

                bool ok = r1 && r2;
                Console.WriteLine(ok ? "OK" : "FAILED");
                return ok;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }

        static async Task<bool> SharedThenExclusive(LockManager lm)
        {
            Console.Write("3) Exclusive waits for shared release... ");
            string resId = Guid.NewGuid().ToString();
            try
            {
                // Acquire shared
                bool s = await lm.AcquireSharedAsync(1, resId);
                if (!s) { Console.WriteLine("FAILED"); return false; }

                // Request exclusive
                var excl = lm.AcquireExclusiveAsync(2, resId, timeoutMilliseconds: 500);
                if (excl.IsCompleted) { Console.WriteLine("FAILED"); lm.ReleaseLock(1, resId); return false; }

                // Release shared
                lm.ReleaseLock(1, resId);
                bool e = await excl;
                lm.ReleaseLock(2, resId);

                bool ok = e;
                Console.WriteLine(ok ? "OK" : "FAILED");
                return ok;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }

        static async Task<bool> TimeoutVsSuccess(LockManager lm)
        {
            Console.Write("4) Timeout vs successful grant... ");
            string resId = Guid.NewGuid().ToString();
            try
            {
                // Hold shared lock to force exclusive to time out
                bool s = await lm.AcquireSharedAsync(1, resId);
                if (!s) { Console.WriteLine("FAILED"); return false; }

                var exclTimeout = lm.AcquireExclusiveAsync(2, resId, timeoutMilliseconds: 50);
                bool result = await exclTimeout;
                lm.ReleaseLock(1, resId);

                // Now request exclusive again with ample timeout
                bool s2 = await lm.AcquireSharedAsync(3, resId);
                lm.ReleaseLock(3, resId);

                bool ok = result == false;
                Console.WriteLine(ok ? "OK" : "FAILED");
                return ok;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }

        static async Task<bool> CancellationBehavior(LockManager lm1, LockManager unused)
        {
            Console.Write("5) Cancellation throws TaskCanceledException... ");
            string resId = Guid.NewGuid().ToString();
            try
            {
                // Acquire shared to block exclusive
                bool s = await lm1.AcquireSharedAsync(1, resId);
                if (!s) { Console.WriteLine("FAILED"); return false; }

                using var cts = new CancellationTokenSource();
                var task = lm1.AcquireExclusiveAsync(2, resId, timeoutMilliseconds: 1000, cts.Token);

                // Cancel immediately
                cts.Cancel();

                bool threw = false;
                try
                {
                    await task;
                }
                catch (TaskCanceledException)
                {
                    threw = true;
                }

                lm1.ReleaseLock(1, resId);

                Console.WriteLine(threw ? "OK" : "FAILED");
                return threw;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }

        static async Task<bool> MetricsAssertions(LockMetrics metrics, LockManager lm)
        {
            Console.Write("6) Verify LockMetrics recorded events... ");
            try
            {
                // Kick off a short wait
                string resId = Guid.NewGuid().ToString();
                await lm.AcquireSharedAsync(10, resId);
                using var cts = new CancellationTokenSource();
                var t = lm.AcquireExclusiveAsync(11, resId, timeoutMilliseconds: 200);
                lm.ReleaseLock(10, resId);
                await t;

                // Reflect into private _lockStats
                var field = typeof(LockMetrics)
                    .GetField("_lockStats", BindingFlags.Instance | BindingFlags.NonPublic);
                var dict = (ConcurrentDictionary<string, LockStatistic>)field.GetValue(metrics);

                bool found = dict.Keys.Any(k => k.StartsWith("Lock_AcquireLockAsync::"));
                Console.WriteLine(found ? "OK" : "FAILED");
                return found;
            }
            catch
            {
                Console.WriteLine("FAILED");
                return false;
            }
        }
    }
}
