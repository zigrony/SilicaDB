using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Evictions;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.EvictionCacheTester
{
    class Program
    {
        static async Task Main()
        {
            Console.WriteLine("=== SilicaDB Eviction Cache Tester ===\n");

            await RunSuite("Invalid Arguments", TestInvalidArguments);
            await RunSuite("SizeCache Basic", TestEvictionSizeCache);
            await RunSuite("TimeCache Basic", TestEvictionTimeCache);
            await RunSuite("LRUCache Basic", TestEvictionLruCache);
            await RunSuite("LRU Order Eviction", TestEvictionLruOrdering);
            await RunSuite("TimeCache Access Update", TestEvictionTimeAccessUpdates);
            await RunSuite("Factory Exception Handling", TestFactoryException);
            await RunSuite("Concurrency Stress", TestConcurrencyStress);

            Console.WriteLine("\nAll tests finished. Press ENTER to exit.");
            Console.ReadLine();
        }

        static async Task RunSuite(string name, Func<Task> suite)
        {
            Console.Write($"--- {name,-30} ");
            var sw = Stopwatch.StartNew();
            try
            {
                await suite();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            }
            finally
            {
                Console.ResetColor();
            }
        }

        // 1) Invalid constructor arguments
        static Task TestInvalidArguments()
        {
            bool thrown;

            // SizeCache: capacity < 1
            thrown = false;
            try { _ = new EvictionSizeCache<int, int>(0, k => ValueTask.FromResult(0), (_, _) => ValueTask.CompletedTask); }
            catch (ArgumentOutOfRangeException) { thrown = true; }
            if (!thrown) throw new Exception("SizeCache: capacity <1 should throw.");

            // SizeCache: null factory
            thrown = false;
            try { _ = new EvictionSizeCache<int, int>(1, null!, (_, _) => ValueTask.CompletedTask); }
            catch (ArgumentNullException) { thrown = true; }
            if (!thrown) throw new Exception("SizeCache: null factory should throw.");

            // SizeCache: null onEvicted
            thrown = false;
            try { _ = new EvictionSizeCache<int, int>(1, k => ValueTask.FromResult(0), null!); }
            catch (ArgumentNullException) { thrown = true; }
            if (!thrown) throw new Exception("SizeCache: null onEvicted should throw.");

            // LRUCache: capacity < 1
            thrown = false;
            try { _ = new EvictionLruCache<int, int>(0, TimeSpan.FromSeconds(1), k => ValueTask.FromResult(0), (_, _) => ValueTask.CompletedTask); }
            catch (ArgumentOutOfRangeException) { thrown = true; }
            if (!thrown) throw new Exception("LRUCache: capacity <1 should throw.");

            // LRUCache: null factory
            thrown = false;
            try { _ = new EvictionLruCache<int, int>(1, TimeSpan.FromSeconds(1), null!, (_, _) => ValueTask.CompletedTask); }
            catch (ArgumentNullException) { thrown = true; }
            if (!thrown) throw new Exception("LRUCache: null factory should throw.");

            // LRUCache: null onEvicted
            thrown = false;
            try { _ = new EvictionLruCache<int, int>(1, TimeSpan.FromSeconds(1), k => ValueTask.FromResult(0), null!); }
            catch (ArgumentNullException) { thrown = true; }
            if (!thrown) throw new Exception("LRUCache: null onEvicted should throw.");

            return Task.CompletedTask;
        }

        // 2) Capacity-only eviction
        static async Task TestEvictionSizeCache()
        {
            int runs = 0;
            var evicted = new ConcurrentBag<int>();

            var cache = new EvictionSizeCache<int, string>(
                capacity: 3,
                factory: k =>
                {
                    Interlocked.Increment(ref runs);
                    return new ValueTask<string>($"V{k}");
                },
                onEvictedAsync: (k, _) =>
                {
                    evicted.Add(k);
                    return ValueTask.CompletedTask;
                });

            // fill to capacity
            await cache.GetOrAddAsync(1);
            await cache.GetOrAddAsync(2);
            await cache.GetOrAddAsync(3);
            if (runs != 3) throw new Exception("Factory should run 3× initially.");

            // add 4th => evict key=1
            await cache.GetOrAddAsync(4);
            if (evicted.Count != 1 || !evicted.Contains(1))
                throw new Exception("Expected eviction of key=1.");

            // re-add 1 => capacity exceeded again => evict one more
            evicted = new ConcurrentBag<int>();
            var before = runs;
            await cache.GetOrAddAsync(1);

            if (runs != before + 1)
                throw new Exception($"Factory should run once more; expected {before + 1}, got {runs}.");
            if (evicted.Count != 1)
                throw new Exception("Re-adding evicted key must trigger exactly one eviction.");

            await cache.DisposeAsync();
        }

        // 3) Idle-time eviction only
        static async Task TestEvictionTimeCache()
        {
            var idle = TimeSpan.FromMilliseconds(20);
            var evicted = new ConcurrentBag<string>();

            var cache = new EvictionTimeCache<string, int>(
                idleTimeout: idle,
                factory: k => new ValueTask<int>(k.Length),
                onEvictedAsync: (k, _) =>
                {
                    evicted.Add(k);
                    return ValueTask.CompletedTask;
                });

            // prime keys
            await cache.GetOrAddAsync("A");
            await cache.GetOrAddAsync("B");
            await cache.GetOrAddAsync("C");

            // immediate cleanup => nothing
            await cache.CleanupIdleAsync();
            if (evicted.Count != 0)
                throw new Exception("No entries should be evicted immediately.");

            // wait past idle => all must go
            await Task.Delay(idle + TimeSpan.FromMilliseconds(5));
            await cache.CleanupIdleAsync();
            foreach (var key in new[] { "A", "B", "C" })
                if (!evicted.Contains(key))
                    throw new Exception($"Expected '{key}' to be evicted.");

            // second pass => no duplicates
            evicted = new ConcurrentBag<string>();
            await cache.CleanupIdleAsync();
            if (evicted.Count != 0)
                throw new Exception("No new evictions on second cleanup.");

            await cache.DisposeAsync();
        }

        // 4) Combined LRU semantics
        static async Task TestEvictionLruCache()
        {
            var evicted = new ConcurrentBag<int>();
            var cache = new EvictionLruCache<int, Guid>(
                capacity: 2,
                idleTimeout: TimeSpan.FromSeconds(1),
                factory: k => new ValueTask<Guid>(Guid.NewGuid()),
                onEvictedAsync: (k, _) =>
                {
                    evicted.Add(k);
                    return ValueTask.CompletedTask;
                });

            // add 10,20 => touch 10 => add 30 => evict 20
            await cache.GetOrAddAsync(10);
            await cache.GetOrAddAsync(20);
            await cache.GetOrAddAsync(10);
            await cache.GetOrAddAsync(30);
            if (!evicted.Contains(20))
                throw new Exception("LRU eviction should remove 20 first.");

            await cache.DisposeAsync();
        }

        // 5) Verify LRU reordering on access
        static async Task TestEvictionLruOrdering()
        {
            var evicted = new ConcurrentBag<int>();
            var cache = new EvictionLruCache<int, int>(
                capacity: 3,
                idleTimeout: TimeSpan.FromSeconds(1),
                factory: k => new ValueTask<int>(k),
                onEvictedAsync: (k, _) =>
                {
                    evicted.Add(k);
                    return ValueTask.CompletedTask;
                });

            // add 1,2,3 => access 1 and 2 => add 4 => evict 3
            await cache.GetOrAddAsync(1);
            await cache.GetOrAddAsync(2);
            await cache.GetOrAddAsync(3);
            await cache.GetOrAddAsync(1);
            await cache.GetOrAddAsync(2);
            await cache.GetOrAddAsync(4);

            if (!evicted.Contains(3))
                throw new Exception("Expected eviction of key=3 (LRU).");

            await cache.DisposeAsync();
        }

        // 6) Verify time-based eviction respects access updates
        static async Task TestEvictionTimeAccessUpdates()
        {
            var idle = TimeSpan.FromMilliseconds(20);
            var evicted = new ConcurrentBag<string>();

            var cache = new EvictionTimeCache<string, int>(
                idleTimeout: idle,
                factory: k => new ValueTask<int>(k.Length),
                onEvictedAsync: (k, _) =>
                {
                    evicted.Add(k);
                    return ValueTask.CompletedTask;
                });

            await cache.GetOrAddAsync("A");
            await cache.GetOrAddAsync("B");

            // wait half the idle, then access B again
            await Task.Delay(idle / 2);
            await cache.GetOrAddAsync("B");

            // wait past idle
            await Task.Delay(idle / 2 + TimeSpan.FromMilliseconds(5));
            await cache.CleanupIdleAsync();

            if (!evicted.Contains("A"))
                throw new Exception("A should have been evicted.");
            if (evicted.Contains("B"))
                throw new Exception("B should NOT have been evicted.");

            await cache.DisposeAsync();
        }

        // 7) Ensure factory exceptions propagate and don't poison cache
        static async Task TestFactoryException()
        {
            // SizeCache
            var cache1 = new EvictionSizeCache<int, int>(
                capacity: 1,
                factory: k =>
                {
                    if (k == 42) throw new InvalidOperationException("boom");
                    return new ValueTask<int>(k);
                },
                onEvictedAsync: (_, _) => ValueTask.CompletedTask);

            bool thrown = false;
            try { await cache1.GetOrAddAsync(42); }
            catch (InvalidOperationException) { thrown = true; }
            if (!thrown) throw new Exception("SizeCache: did not propagate factory exception.");

            // still works for new key
            var v1 = await cache1.GetOrAddAsync(7);
            if (v1 != 7) throw new Exception("SizeCache: subsequent factory did not run.");

            await cache1.DisposeAsync();

            // TimeCache
            var cache2 = new EvictionTimeCache<int, int>(
                idleTimeout: TimeSpan.FromSeconds(1),
                factory: k =>
                {
                    if (k == 99) throw new InvalidOperationException("boom2");
                    return new ValueTask<int>(k);
                },
                onEvictedAsync: (_, _) => ValueTask.CompletedTask);

            thrown = false;
            try { await cache2.GetOrAddAsync(99); }
            catch (InvalidOperationException) { thrown = true; }
            if (!thrown) throw new Exception("TimeCache: did not propagate factory exception.");

            var v2 = await cache2.GetOrAddAsync(5);
            if (v2 != 5) throw new Exception("TimeCache: subsequent factory did not run.");

            await cache2.DisposeAsync();

            // LRUCache
            var cache3 = new EvictionLruCache<int, int>(
                capacity: 2,
                idleTimeout: TimeSpan.FromSeconds(1),
                factory: k =>
                {
                    if (k == 11) throw new InvalidOperationException("boom3");
                    return new ValueTask<int>(k);
                },
                onEvictedAsync: (_, _) => ValueTask.CompletedTask);

            thrown = false;
            try { await cache3.GetOrAddAsync(11); }
            catch (InvalidOperationException) { thrown = true; }
            if (!thrown) throw new Exception("LRUCache: did not propagate factory exception.");

            var v3 = await cache3.GetOrAddAsync(8);
            if (v3 != 8) throw new Exception("LRUCache: subsequent factory did not run.");

            await cache3.DisposeAsync();
        }

        // 8) Concurrency stress test (GetOrAdd + CleanupIdle)
        static async Task TestConcurrencyStress()
        {
            var rnd = new Random();
            IAsyncEvictionCache<int, int>[] caches = new IAsyncEvictionCache<int, int>[]
            {
                new EvictionSizeCache<int,int>(5, k => new ValueTask<int>(k), (_,_) => ValueTask.CompletedTask),
                new EvictionTimeCache<int,int>(TimeSpan.FromMilliseconds(50), k => new ValueTask<int>(k), (_,_) => ValueTask.CompletedTask),
                new EvictionLruCache<int,int>(5, TimeSpan.FromMilliseconds(50), k => new ValueTask<int>(k), (_,_) => ValueTask.CompletedTask)
            };

            var allTasks = caches.Select(cache => Task.Run(async () =>
            {
                var tasks = Enumerable.Range(0, 500).Select(async i =>
                {
                    int key = rnd.Next(0, 10);
                    await cache.GetOrAddAsync(key);
                    if (rnd.NextDouble() < 0.1)
                        await cache.CleanupIdleAsync();
                });

                await Task.WhenAll(tasks);
                await cache.DisposeAsync();
            }));

            await Task.WhenAll(allTasks);
        }
    }
}
