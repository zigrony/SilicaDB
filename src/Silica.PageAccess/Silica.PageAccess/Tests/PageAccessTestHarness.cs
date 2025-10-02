using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.PageAccess;
using Silica.DiagnosticsCore.Metrics;
using Silica.Common.Primitives;

namespace Silica.PageAccess.Tests
{
    // No LINQ, no reflection, no JSON, no third-party libraries.
    public static class PageAccessTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== PageAccess Test Harness ===");

            int pageSize = 4096;
            uint magic = 0xC0DEC0DE;
            var version = new PageVersion(1, 0);
            var metrics = new DummyMetricsManager();
            var options = PageAccessorOptions.Default(magic);
            var id = new PageId(1, 42);

            var device = new InMemoryPageDevice(pageSize);
            await using var pool = new BufferPoolManager(device, metrics, wal: null, logFlush: null, poolName: "PageAccessPool", capacityPages: 32);
            var allocator = new Silica.StorageAllocation.StubAllocator();
            await using var pageAccess = new PageManager(pool, options, metrics, allocator, "PageAccessHarness", walHook: null);

            await RunTest("[Test] Header bootstrap + read validation", () => TestHeaderBootstrapAndRead(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Read/Write lease roundtrip (body slice)", () => TestReadWriteLeaseRoundtrip(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Header-only mutation via WriteHeaderAsync", () => TestHeaderOnlyMutation(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Full-page checksum update + verification", () => TestChecksumUpdateAndVerify(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Flush and eviction with clean page", () => TestFlushAndEvictClean(device, pageAccess, pool, id, version, magic, pageSize));
            await RunTest("[Test] Flush dirty page path", () => TestFlushDirtyPage(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Error paths (bad magic/type/version, invalid ranges)", () => TestErrorPaths(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] Concurrency: multiple readers, single writer overlap", () => TestReadersWritersOverlap(device, pageAccess, id, version, magic, pageSize));
            await RunTest("[Test] WriteAndStampAsync (WAL hook LSN stamping)", () => TestWriteAndStampWithWalHook(device, pool, id, version, magic, pageSize));

            Console.WriteLine("=== PageAccess Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Func<Task> test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            await test();
            sw.Stop();
            Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        // ------------------------------------------------------------
        // Tests
        // ------------------------------------------------------------

        private static async Task TestHeaderBootstrapAndRead(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            var initial = new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0);

            // Raw bootstrap of header into device before any PageAccess calls
            await BootstrapHeaderRaw(device, id, pageSize, initial);

            var hdrWritten = await pa.WriteHeaderAsync(id, ver, PageType.Data, _ => initial);
            if (hdrWritten.Magic != magic) throw new Exception("Magic mismatch after bootstrap header write.");

            await using var r = await pa.ReadAsync(id, ver, PageType.Data);
            var hdr = r.Header;
            if (hdr.Magic != magic) throw new Exception("Magic mismatch on read.");
            if (hdr.Version.Major != ver.Major || hdr.Version.Minor != ver.Minor) throw new Exception("Version mismatch on read.");
            if (hdr.Type != PageType.Data) throw new Exception("Type mismatch on read.");
            if (hdr.FreeSpaceOffset < PageHeader.HeaderSize) throw new Exception("FreeSpaceOffset invariant violated.");
        }

        private static async Task TestReadWriteLeaseRoundtrip(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            int offset = PageHeader.HeaderSize + 8;
            int length = 64;
            byte pattern = 0x5A;

            var w = await pa.WriteAsync(id, offset, length, ver, PageType.Data);
            FillPattern(w.Slice.Span, pattern);
            await w.DisposeAsync();

            await using var r = await pa.ReadAsync(id, ver, PageType.Data);
            VerifyPattern(r.Page.Span, offset, length, pattern);
        }

        private static async Task TestHeaderOnlyMutation(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            var before = await pa.WriteHeaderAsync(id, ver, PageType.Data, h => h);
            ushort nextCount = (ushort)(before.RowCount + 1);
            var after = await pa.WriteHeaderAsync(id, ver, PageType.Data, h => h.With(rowCount: nextCount));
            if (after.RowCount != nextCount) throw new Exception("Header-only mutation failed to update RowCount.");
        }

        private static async Task TestChecksumUpdateAndVerify(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            var w = await pa.WriteAsync(id, 0, pageSize, ver, PageType.Data);
            var hdr = new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 7, 0, 0, 0);
            WriteHeaderSync(w.Slice.Span, hdr);
            FillAndChecksum(w.Slice.Span);
            await w.DisposeAsync();

            await using var r = await pa.ReadAsync(id, ver, PageType.Data);
            uint expected, actual;
            if (!PageManager.VerifyChecksum(r.Page.Span, out expected, out actual))
                throw new Exception($"Checksum verification failed: expected={expected:X8} actual={actual:X8}");
        }

        private static async Task TestFlushAndEvictClean(IPageDevice device, PageManager pa, BufferPoolManager pool, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            await pa.WriteHeaderAsync(id, ver, PageType.Data, h => h);
            await pa.FlushPageAsync(id);

            bool evicted = await pool.TryEvictAsync(id);
            if (!evicted) throw new Exception("Clean eviction failed (expected true).");
        }

        private static async Task TestFlushDirtyPage(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            var updated = await pa.WriteHeaderAsync(id, ver, PageType.Data, h => h.With(rowCount: (ushort)(h.RowCount + 5)));
            if (updated.RowCount == 0) throw new Exception("Failed to dirty page via header increment.");
            await pa.FlushPageAsync(id);
        }

        private static async Task TestErrorPaths(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            bool threw = false;
            try { var _ = await pa.WriteAsync(id, PageHeader.HeaderSize, 0, ver, PageType.Data); }
            catch (ArgumentOutOfRangeException) { threw = true; } // PageManager throws ArgumentOutOfRange for zero length
            if (!threw) throw new Exception("Expected ArgumentOutOfRangeException for zero-length write.");

            threw = false;
            try { await using var r = await pa.ReadAsync(id, ver, PageType.Index); }
            catch (PageTypeMismatchException) { threw = true; }
            if (!threw) throw new Exception("Expected PageTypeMismatchException when reading with wrong expected type.");

            // New device/pool intentionally writes bad magic (no bootstrap by design)
            var device2 = new InMemoryPageDevice(pageSize);
            await using var pool2 = new BufferPoolManager(device2, new DummyMetricsManager(), wal: null, logFlush: null, poolName: "ErrPool", capacityPages: 8);
            var allocator2 = new Silica.StorageAllocation.StubAllocator();
            await using var pa2 = new PageManager(pool2, PageAccessorOptions.Default(magic), new DummyMetricsManager(), allocator2, "ErrAccess", walHook: null);

            var badHdr = new PageHeader(0xDEADBEEF, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0);
            await BootstrapHeaderRaw(device2, id, pageSize, badHdr);

            threw = false;
            try { await using var r2 = await pa2.ReadAsync(id, ver, PageType.Data); }
            catch (MagicMismatchException) { threw = true; }
            if (!threw) throw new Exception("Expected MagicMismatchException for bad magic.");
        }

        private static async Task TestReadersWritersOverlap(IPageDevice device, PageManager pa, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            await BootstrapHeaderRaw(device, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            int readers = 4;
            int writerLen = 128;
            int writerOffset = PageHeader.HeaderSize + 32;

            Task[] tasks = new Task[readers + 1];
            for (int i = 0; i < readers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    for (int k = 0; k < 20; k++)
                    {
                        await using var r = await pa.ReadAsync(id, ver, PageType.Data);
                        var _ = r.Page.Span[PageHeader.HeaderSize];
                        await Task.Yield();
                    }
                });
            }
            tasks[readers] = Task.Run(async () =>
            {
                var w = await pa.WriteAsync(id, writerOffset, writerLen, ver, PageType.Data);
                FillPattern(w.Slice.Span, 0xA5);
                await Task.Delay(10);
                await w.DisposeAsync();
            });

            await Task.WhenAll(tasks);
        }

        private static async Task TestWriteAndStampWithWalHook(IPageDevice device, BufferPoolManager pool, PageId id, PageVersion ver, uint magic, int pageSize)
        {
            var metrics = new DummyMetricsManager();
            var options = PageAccessorOptions.Default(magic);
            var device2 = new InMemoryPageDevice(pageSize);
            await using var pool2 = new BufferPoolManager(device2, metrics, wal: null, logFlush: null, poolName: "WalHookPool", capacityPages: 8);
            var hook = new FakeWalHook();
            var allocator2 = new Silica.StorageAllocation.StubAllocator();
            await using var pa = new PageManager(pool2, options, metrics, allocator2, "WalHookAccess", walHook: hook);

            // Bootstrap header on the wal-hook device
            await BootstrapHeaderRaw(device2, id, pageSize, new PageHeader(magic, 0, ver, PageType.Data, false, PageHeader.HeaderSize, 0, 0, 0, 0));

            int off = PageHeader.HeaderSize + 64;
            int len = 32;
            var stamped = await pa.WriteAndStampAsync(id, off, len, ver, PageType.Data, (mem, hdr) =>
            {
                FillPattern(mem.Span, 0xEE);
                return hdr;
            });

            if (stamped.Lsn == 0) throw new Exception("WriteAndStampAsync did not stamp a non-zero LSN.");
            if (hook.LastLsn == 0 || (ulong)stamped.Lsn != hook.LastLsn) throw new Exception("Stamped LSN does not match hook LSN.");

            await using var r = await pa.ReadAsync(id, ver, PageType.Data);
            VerifyPattern(r.Page.Span, off, len, 0xEE);
        }

        // ------------------------------------------------------------
        // Raw bootstrap helper (first touch)
        // ------------------------------------------------------------

        private static async Task BootstrapHeaderRaw(IPageDevice device, PageId id, int pageSize, PageHeader header)
        {
            var buf = new byte[pageSize];
            PageHeader.Write(buf.AsSpan(), header);
            await device.WritePageAsync(id, buf, CancellationToken.None);
        }

        // ------------------------------------------------------------
        // Synchronous Span helpers (avoid ref struct in async methods)
        // ------------------------------------------------------------

        private static void WriteHeaderSync(Span<byte> span, PageHeader hdr)
        {
            PageHeader.Write(span, hdr);
        }

        private static void FillAndChecksum(Span<byte> span)
        {
            int bodyStart = PageHeader.HeaderSize;
            for (int i = bodyStart; i < span.Length; i++)
                span[i] = (byte)((i * 37) & 0xFF);

            PageManager.UpdateChecksumInPlace(span);
        }

        private static void FillPattern(Span<byte> span, byte pattern)
        {
            int n = span.Length;
            for (int i = 0; i < n; i++)
                span[i] = pattern;
        }

        private static void VerifyPattern(ReadOnlySpan<byte> span, int offset, int length, byte pattern)
        {
            for (int i = 0; i < length; i++)
            {
                if (span[offset + i] != pattern)
                    throw new Exception("Pattern mismatch at " + i.ToString());
            }
        }

        // ------------------------------------------------------------
        // Minimal in-memory page device (no disk I/O)
        // ------------------------------------------------------------

        private sealed class InMemoryPageDevice : IPageDevice
        {
            private readonly int _pageSize;
            private readonly object _sync = new object();
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

            public Task ReadPageAsync(PageId pageId, Memory<byte> destination, CancellationToken ct)
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

            public Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, CancellationToken ct)
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

        private sealed class FakeWalHook : IPageWalHook
        {
            private ulong _last;
            public ulong LastLsn => _last;

            public ValueTask<ulong> OnBeforePageWriteAsync(PageId pageId, int offset, int length, ReadOnlyMemory<byte> afterImage, CancellationToken ct)
            {
                _last = _last == 0 ? 1UL : _last + 1UL;
                return new ValueTask<ulong>(_last);
            }
        }

        private sealed class DummyMetricsManager : IMetricsManager
        {
            public void Register(MetricDefinition def) { }
            public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }
    }
}
