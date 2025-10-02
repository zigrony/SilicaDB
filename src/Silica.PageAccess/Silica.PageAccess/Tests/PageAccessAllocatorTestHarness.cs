using System;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Common.Primitives;
using Silica.Common.Contracts;
using Silica.PageAccess;
using Silica.StorageAllocation;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.PageAccess.Tests
{
    public static class PageAccessAllocatorTestHarness
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("=== PageAccessAllocatorTestHarness starting ===");

            var metrics = new DummyMetricsManager();
            var device = new InMemoryPageDevice(8192);
            var pool = new BufferPoolManager(
                device,
                metrics,
                wal: null,
                logFlush: null,
                poolName: "AllocatorHarness",
                capacityPages: 32);

            var allocator = new StubAllocator();

            await using var pageAccess = new PageManager(
                pool,
                new PageAccessorOptions(),
                metrics,
                allocator,
                "AllocatorHarness");

            // 1. Allocate a new page
            var pageId = await pageAccess.CreatePageAsync();
            Console.WriteLine($"Allocated PageId: File={pageId.FileId}, Index={pageId.PageIndex}");

            // 2. Stamp a valid header
            var header = new PageHeader(
                magic: 0xC0DEC0DE,
                checksum: 0,
                version: new PageVersion(1, 0),
                type: PageType.Data,
                hasRowVersioning: false,
                freeSpaceOffset: PageHeader.HeaderSize, // must be >= header size
                rowCount: 0,
                lsn: 0,
                nextFileId: 0,
                nextPageIndex: 0
            );

            var stampedHeader = await pageAccess.WriteHeaderAsync(
                pageId,
                expectedVersion: default,
                expectedType: default,
                mutate: _ => header);

            Console.WriteLine($"Stamped header: Type={stampedHeader.Type}, Version={stampedHeader.Version}");

            // 3. Read it back
            await using var readHandle = await pageAccess.ReadAsync(
                pageId,
                stampedHeader.Version,
                stampedHeader.Type);

            Console.WriteLine($"Read back header: Type={readHandle.Header.Type}, Version={readHandle.Header.Version}");

            // 4. Verify
            if (readHandle.Header.Type == PageType.Data &&
                readHandle.Header.Version.Equals(new PageVersion(1, 0)))
            {
                Console.WriteLine("Smoke test PASSED ✅");
            }
            else
            {
                Console.WriteLine("Smoke test FAILED ❌");
            }

            Console.WriteLine("=== PageAccessAllocatorTestHarness finished ===");
        }
    }

    // Simple no-op metrics manager for harness use
    internal sealed class DummyMetricsManager : IMetricsManager
    {
        public void Register(MetricDefinition def) { }
        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
        public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
        public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
    }

    // In-memory page device for harness use
    internal sealed class InMemoryPageDevice : IPageDevice
    {
        private readonly int _pageSize;
        private readonly object _sync = new();
        private PageBuffer[] _pages;
        private int _count;

        private struct PageBuffer
        {
            public long FileId;
            public long PageIndex;
            public byte[] Bytes;
        }

        public InMemoryPageDevice(int pageSize)
        {
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
            _pageSize = pageSize;
            _pages = new PageBuffer[64];
            _count = 0;
        }

        public int PageSize => _pageSize;

        public Task ReadPageAsync(PageId pageId, Memory<byte> destination, System.Threading.CancellationToken ct)
        {
            if (destination.Length != _pageSize) throw new ArgumentOutOfRangeException(nameof(destination));
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                int idx = Find(pageId);
                if (idx >= 0)
                    new Span<byte>(_pages[idx].Bytes).CopyTo(destination.Span);
                else
                    destination.Span.Clear();
            }
            return Task.CompletedTask;
        }

        public Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, System.Threading.CancellationToken ct)
        {
            if (source.Length != _pageSize) throw new ArgumentOutOfRangeException(nameof(source));
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                int idx = Find(pageId);
                if (idx < 0)
                {
                    EnsureCapacity(_count + 1);
                    idx = _count++;
                    _pages[idx].FileId = pageId.FileId;
                    _pages[idx].PageIndex = pageId.PageIndex;
                    _pages[idx].Bytes = new byte[_pageSize];
                }
                source.Span.CopyTo(_pages[idx].Bytes);
            }
            return Task.CompletedTask;
        }

        private int Find(PageId id)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_pages[i].FileId == id.FileId && _pages[i].PageIndex == id.PageIndex)
                    return i;
            }
            return -1;
        }

        private void EnsureCapacity(int need)
        {
            if (need <= _pages.Length) return;
            int newCap = _pages.Length * 2;
            if (newCap < need) newCap = need;
            var arr = new PageBuffer[newCap];
            for (int i = 0; i < _count; i++) arr[i] = _pages[i];
            _pages = arr;
        }
    }
}
