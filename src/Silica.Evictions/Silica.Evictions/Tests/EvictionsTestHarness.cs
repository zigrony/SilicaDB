using System;
using System.Threading.Tasks;
using Silica.Evictions;

namespace Silica.Evictions.Tests
{
    public static class EvictionsTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Silica.Evictions Test Harness ===");

            await TestCache("EvictionClockCache", () =>
                new EvictionClockCache<string, string>(
                    capacity: 3,
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[Clock] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionFifoCache", () =>
                new EvictionFifoCache<string, string>(
                    capacity: 3,
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[FIFO] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionLfuCache", () =>
                new EvictionLfuCache<string, string>(
                    capacity: 3,
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[LFU] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionLruCache", () =>
                new EvictionLruCache<string, string>(
                    capacity: 3,
                    idleTimeout: TimeSpan.FromSeconds(5),
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[LRU] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionMruCache", () =>
                new EvictionMruCache<string, string>(
                    capacity: 3,
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[MRU] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionSizeCache", () =>
                new EvictionSizeCache<string, string>(
                    capacity: 3,
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[Size] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            await TestCache("EvictionTimeCache", () =>
                new EvictionTimeCache<string, string>(
                    idleTimeout: TimeSpan.FromSeconds(1),
                    factory: k => ValueTask.FromResult("Value_" + k),
                    onEvictedAsync: (k, v) => { Console.WriteLine($"[Time] Evicted {k}={v}"); return ValueTask.CompletedTask; }
                ));

            Console.WriteLine("=== Test Harness Complete ===");
        }

        private static async Task TestCache<TCache>(string name, Func<TCache> factory)
            where TCache : IAsyncDisposable
        {
            Console.WriteLine($"\n--- Testing {name} ---");

            var cache = factory();

            try
            {
                for (int i = 1; i <= 3; i++)
                {
                    var key = "K" + i;
                    var value = await GetOrAdd(cache, key);
                    Console.WriteLine($"Added {key}={value}");
                }

                for (int i = 4; i <= 5; i++)
                {
                    var key = "K" + i;
                    var value = await GetOrAdd(cache, key);
                    Console.WriteLine($"Added {key}={value}");
                }

                await GetOrAdd(cache, "K2");
                await GetOrAdd(cache, "K3");

                await CleanupIdle(cache);

                Console.WriteLine($"Final Count: {GetCount(cache)}");
            }
            finally
            {
                await cache.DisposeAsync();
            }
        }

        private static ValueTask<string> GetOrAdd<TCache>(TCache cache, string key)
        {
            if (cache is EvictionClockCache<string, string> clock) return clock.GetOrAddAsync(key);
            if (cache is EvictionFifoCache<string, string> fifo) return fifo.GetOrAddAsync(key);
            if (cache is EvictionLfuCache<string, string> lfu) return lfu.GetOrAddAsync(key);
            if (cache is EvictionLruCache<string, string> lru) return lru.GetOrAddAsync(key);
            if (cache is EvictionMruCache<string, string> mru) return mru.GetOrAddAsync(key);
            if (cache is EvictionSizeCache<string, string> size) return size.GetOrAddAsync(key);
            if (cache is EvictionTimeCache<string, string> time) return time.GetOrAddAsync(key);
            throw new NotSupportedException("Unknown cache type");
        }

        private static ValueTask CleanupIdle<TCache>(TCache cache)
        {
            if (cache is EvictionClockCache<string, string> clock) return clock.CleanupIdleAsync();
            if (cache is EvictionFifoCache<string, string> fifo) return fifo.CleanupIdleAsync();
            if (cache is EvictionLfuCache<string, string> lfu) return lfu.CleanupIdleAsync();
            if (cache is EvictionLruCache<string, string> lru) return lru.CleanupIdleAsync();
            if (cache is EvictionMruCache<string, string> mru) return mru.CleanupIdleAsync();
            if (cache is EvictionSizeCache<string, string> size) return size.CleanupIdleAsync();
            if (cache is EvictionTimeCache<string, string> time) return time.CleanupIdleAsync();
            return ValueTask.CompletedTask;
        }

        private static int GetCount<TCache>(TCache cache)
        {
            if (cache is EvictionClockCache<string, string> clock) return clock.Count;
            if (cache is EvictionFifoCache<string, string> fifo) return fifo.Count;
            if (cache is EvictionLfuCache<string, string> lfu) return lfu.Count;
            if (cache is EvictionLruCache<string, string> lru) return lru.Count;
            if (cache is EvictionMruCache<string, string> mru) return mru.Count;
            if (cache is EvictionSizeCache<string, string> size) return size.Count;
            if (cache is EvictionTimeCache<string, string> time) return time.Count;
            return -1;
        }
    }
}
