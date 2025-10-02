using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.DiagnosticsCore.Metrics;
using Silica.Storage;
using Silica.Common.Primitives;

namespace Silica.BufferPool.Tests
{
    /// <summary>
    /// Test harness for exercising BufferPoolManager behaviors.
    /// Includes core lifecycle, diagnostics parity, and concurrency orchestration tests.
    /// </summary>
    public static class BufferPoolTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== BufferPool Test Harness ===");

            IPageDevice device = new DummyPageDevice();
            IMetricsManager metrics = new DummyMetricsManager();

            var bufferPool = new BufferPoolManager(
                device,
                metrics,
                wal: null,
                logFlush: null,
                poolName: "TestPool",
                capacityPages: 16
            );

            // Enable verbose diagnostics (so you can see flush/evict events in logs)
            BufferPool.Diagnostics.BufferPoolDiagnostics.SetVerbose(true);

            await TestPageReadWriteLease(bufferPool);
            await TestDirtyPageTracking(bufferPool);
            await TestFlushAndEviction(bufferPool);
            await TestDiagnosticsParity(bufferPool);
            await TestConcurrencyOrchestration(bufferPool);

            Console.WriteLine("=== BufferPool Test Harness Complete ===");
        }

        private static async Task TestPageReadWriteLease(BufferPoolManager bufferPool)
        {
            Console.WriteLine("[Test] Page Read/Write Lease");

            var pageId = new PageId(1, 0);

            // Acquire a write lease, modify, mark dirty, release
            await using (var lease = await bufferPool.AcquireWriteAsync(pageId, 0, 4096))
            {
                lease.Slice.Span[0] = 42;
                lease.MarkDirty(100); // dummy LSN
            }

            // Acquire a read lease, verify value
            await using (var lease = await bufferPool.AcquireReadAsync(pageId))
            {
                if (lease.Page.Span[0] != 42)
                    throw new InvalidOperationException("Page value mismatch after write.");
            }

            Console.WriteLine("Page read/write lease test passed.");
        }

        private static async Task TestDirtyPageTracking(BufferPoolManager bufferPool)
        {
            Console.WriteLine("[Test] Dirty Page Tracking");

            var pageId = new PageId(1, 1);

            // Write and mark dirty
            await using (var lease = await bufferPool.AcquireWriteAsync(pageId, 0, 4096))
            {
                lease.Slice.Span[0] = 99;
                lease.MarkDirty(200); // dummy LSN
            }

            // Check dirty pages
            var dirty = bufferPool.SnapshotDirtyPages();
            if (!dirty.Any(d => d.PageId.Equals(pageId)))
                throw new InvalidOperationException("Dirty page not tracked.");

            Console.WriteLine("Dirty page tracking test passed.");
        }

        private static async Task TestFlushAndEviction(BufferPoolManager bufferPool)
        {
            Console.WriteLine("[Test] Flush and Eviction");

            var pageId = new PageId(1, 2);

            // Write and mark dirty
            await using (var lease = await bufferPool.AcquireWriteAsync(pageId, 0, 4096))
            {
                lease.Slice.Span[0] = 123;
                lease.MarkDirty(300);
            }

            // Flush
            await bufferPool.FlushPageAsync(pageId);

            // After flush, page should not be dirty
            var dirty = bufferPool.SnapshotDirtyPages();
            if (dirty.Any(d => d.PageId.Equals(pageId)))
                throw new InvalidOperationException("Page flush did not clear dirty flag.");

            // Try evict
            bool evicted = await bufferPool.TryEvictAsync(pageId);
            if (!evicted)
                throw new InvalidOperationException("Page eviction failed.");

            Console.WriteLine("Flush and eviction test passed.");
        }

        private static async Task TestDiagnosticsParity(BufferPoolManager bufferPool)
        {
            Console.WriteLine("[Test] Diagnostics Parity");

            var pageId = new PageId(1, 3);

            // Write and mark dirty
            await using (var lease = await bufferPool.AcquireWriteAsync(pageId, 0, 4096))
            {
                lease.Slice.Span[0] = 55;
                lease.MarkDirty(400);
            }

            // Flush
            await bufferPool.FlushPageAsync(pageId);

            // NOTE: In-memory capture of diagnostics is not possible without a code change to BufferPoolDiagnostics.
            // For now, check your logs for flush_begin, device_write_done, flush_done events for this page.
            Console.WriteLine("Diagnostics parity test: check logs for flush_begin, device_write_done, flush_done events for file=1, page=3.");
        }

        private static async Task TestConcurrencyOrchestration(BufferPoolManager bufferPool)
        {
            Console.WriteLine("[Test] Concurrency Orchestration");

            var pageId = new PageId(1, 4);
            int concurrency = 8;
            int iterations = 32;
            var tasks = new List<Task>();

            for (int i = 0; i < concurrency; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (int j = 0; j < iterations; j++)
                    {
                        await using (var lease = await bufferPool.AcquireWriteAsync(pageId, 0, 4096))
                        {
                            lease.Slice.Span[0] = (byte)(j + i);
                            lease.MarkDirty(500 + j + i);
                        }
                        await bufferPool.FlushPageAsync(pageId);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // After all, page should not be dirty
            var dirty = bufferPool.SnapshotDirtyPages();
            if (dirty.Any(d => d.PageId.Equals(pageId)))
                throw new InvalidOperationException("Concurrency orchestration: page should not be dirty after all flushes.");

            Console.WriteLine("Concurrency orchestration test passed.");
        }

        // --- Dummy dependency implementations for harness use only ---

        class DummyPageDevice : IPageDevice
        {
            public int PageSize => 4096;

            public Task ReadPageAsync(PageId pageId, Memory<byte> destination, CancellationToken ct)
            {
                destination.Span.Clear();
                return Task.CompletedTask;
            }

            public Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }

        class DummyMetricsManager : IMetricsManager
        {
            public void Register(MetricDefinition def) { }
            public void Increment(string name, long value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }
    }
}
