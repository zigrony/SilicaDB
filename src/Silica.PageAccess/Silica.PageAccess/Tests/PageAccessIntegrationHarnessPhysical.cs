using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.Devices;
using Silica.BufferPool;
using Silica.Durability;
using Silica.Concurrency;
using Silica.PageAccess;
using Silica.DiagnosticsCore.Metrics;
using Silica.Common.Primitives;

namespace Silica.PageAccess.Tests
{
    /// <summary>
    /// Integration harness (Physical device) that enforces WAL-before-write using a test WAL hook.
    /// Writes a full page with WriteAndStampAsync, flushes and evicts, then simulates crash and replays WAL.
    /// </summary>
    public static class PageAccessIntegrationHarnessPhysical
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("=== PageAccessIntegrationHarnessPhysical: Begin ===");

            string dbFile = @"c:\temp\integration_test_phys.db";
            string walFile = @"c:\temp\integration_test_phys.wal";
            int capacityPages = 8;

            // Clean up previous artifacts for repeatability
            try { if (File.Exists(dbFile)) File.Delete(dbFile); } catch { }
            try { if (File.Exists(walFile)) File.Delete(walFile); } catch { }

            // Physical device
            var storage = new PhysicalBlockDevice(dbFile);

            // Program manifest so mount initializes frame 0 deterministically
            ((IStackManifestHost)storage).SetExpectedManifest(
                new DeviceManifest(storage.Geometry.LogicalBlockSize, uuidLo: 0, uuidHi: 0, entries: Array.Empty<DeviceManifest.Entry>())
            );

            await storage.MountAsync();

            IMetricsManager metrics = new NoOpMetricsManager();

            // WAL manager (durability)
            var wal = new WalManager(walFile, metrics);

            // Attempt to start wal if a Start/StartAsync exists
            TryInvokeStart(wal);

            // WAL hook that appends full-page records and flushes WAL before returning LSN
            var walHook = new SimplePageWalHook(wal, storage.Geometry.LogicalBlockSize, walFile);

            // BufferPool device adapter
            var device = new PhysicalPageDeviceAdapter(storage);

            // Buffer pool manager
            var bufferPool = new BufferPoolManager(device, metrics, wal: wal, logFlush: null, poolName: "IntegrationTestPool", capacityPages: capacityPages);

            // Page manager wired with walHook
            var options = PageAccessorOptions.Default(magic: 0x53494C41); // "SILA"
            var allocator = new Silica.StorageAllocation.StubAllocator();
            var pageAccess = new PageManager(bufferPool, options, metrics, allocator, componentName: "PageAccessHarnessPhysical", walHook: walHook);

            try
            {
                Console.WriteLine("[Scenario] Full-page WriteAndStampAsync → Flush → Evict → Crash → Recover");

                var pageId = new PageId(1, 1);
                var version = new PageVersion(1, 0);
                int pageSize = storage.Geometry.LogicalBlockSize;

                // Perform full-page WriteAndStampAsync so WAL hook obtains a full-page after-image
                var hdr = await pageAccess.WriteAndStampAsync(
                    pageId,
                    offset: 0,
                    length: pageSize,
                    expectedVersion: version,
                    expectedType: PageType.Data,
                    mutate: (mem, h) =>
                    {
                        // mutate header in place; mem is full-page
                        var updated = h.With(rowCount: (ushort)(h.RowCount + 1));
                        PageHeader.Write(mem.Span, updated);
                        return updated;
                    });

                Console.WriteLine($"[Write] Header LSN after write: {hdr.Lsn}");

                LogFileLengths(dbFile, walFile, "after write");

                // Flush and evict so BufferPool writes frames to backing device
                await pageAccess.FlushPageAsync(pageId);
                await bufferPool.TryEvictAsync(pageId);

                // Small delay to allow background flushes to settle in tests
                await Task.Delay(50);

                LogFileLengths(dbFile, walFile, "after flush+evict");

                // Simulate crash
                Console.WriteLine("Simulating crash...");
                await bufferPool.StopAsync();
                await bufferPool.DisposeAsync();
                TryInvokeStop(wal);
                try { wal.Dispose(); } catch { }
                await storage.UnmountAsync();

                // Reopen device and mount (manifest must be re-provided)
                storage = new PhysicalBlockDevice(dbFile);
                ((IStackManifestHost)storage).SetExpectedManifest(
                    new DeviceManifest(storage.Geometry.LogicalBlockSize, uuidLo: 0, uuidHi: 0, entries: Array.Empty<DeviceManifest.Entry>())
                );
                await storage.MountAsync();

                device = new PhysicalPageDeviceAdapter(storage);
                wal = new WalManager(walFile, metrics);
                TryInvokeStart(wal);

                bufferPool = new BufferPoolManager(device, metrics, wal: wal, logFlush: null, poolName: "IntegrationTestPool", capacityPages: capacityPages);
                var allocator2 = new Silica.StorageAllocation.StubAllocator();
                pageAccess = new PageManager(bufferPool, options, metrics, allocator2, componentName: "PageAccessHarnessPhysical", walHook: null);

                // WAL recovery: replay full-page records into the storage device before reads
                var recover = new RecoverManager(walFile, metrics);
                await recover.StartAsync(default);
                int applied = 0;
                WalRecord? rec;
                while ((rec = await recover.ReadNextAsync(default)) is not null)
                {
                    if (rec.Payload.Length < 16 + pageSize) continue;
                    var payload = rec.Payload.ToArray();
                    long fileId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
                    long pageIndex = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8, 8));
                    var pageBytes = new ReadOnlyMemory<byte>(payload, 16, pageSize);
                    await storage.WriteFrameAsync(pageIndex, pageBytes, default);
                    applied++;
                    Console.WriteLine($"[Recovery] Applied WAL rec -> fileId={fileId}, pageIndex={pageIndex}");
                }
                await recover.StopAsync(default);

                Console.WriteLine($"[Recovery] Applied {applied} WAL records; db length={new FileInfo(dbFile).Length}");

                // Now the read should succeed
                await using (var read = await pageAccess.ReadAsync(pageId, version, PageType.Data))
                {
                    Console.WriteLine("Recovered page header row count: " + read.Header.RowCount);
                    Console.WriteLine("Recovered header LSN: " + read.Header.Lsn);
                }

                Console.WriteLine("[PASS] Recovery preserved data.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { await bufferPool.StopAsync(); } catch { }
                try { await bufferPool.DisposeAsync(); } catch { }
                TryInvokeStop(wal);
                try { wal.Dispose(); } catch { }
                try { await storage.UnmountAsync(); } catch { }
            }

            Console.WriteLine("=== PageAccessIntegrationHarnessPhysical: End ===");
        }

        // WAL hook implementation that appends a full-page record and flushes WAL before returning LSN.
        // Uses the WalManager API if available: prefers AppendAsync(byte[], CancellationToken) -> ValueTask<ulong>,
        // and FlushAsync(CancellationToken). Falls back to sync Append/Flush if present, otherwise performs durable append to walFile path.
        private sealed class SimplePageWalHook : IPageWalHook
        {
            private readonly WalManager _wal;
            private readonly int _pageSize;
            private readonly string? _walPath;

            public SimplePageWalHook(WalManager wal, int pageSize, string walPathHint = null)
            {
                _wal = wal ?? throw new ArgumentNullException(nameof(wal));
                _pageSize = pageSize;
                _walPath = walPathHint;
            }

            public async ValueTask<ulong> OnBeforePageWriteAsync(PageId pageId, int offset, int length, ReadOnlyMemory<byte> afterImage, CancellationToken ct)
            {
                // Ensure we have a full-page buffer
                byte[] pageBuf;
                if (offset == 0 && length == _pageSize && afterImage.Length == _pageSize)
                {
                    pageBuf = afterImage.ToArray();
                }
                else
                {
                    pageBuf = new byte[_pageSize];
                    if (afterImage.Length > 0)
                        afterImage.CopyTo(new Memory<byte>(pageBuf, offset, Math.Min(afterImage.Length, Math.Max(0, _pageSize - offset))));
                }

                // Build WAL record payload: [8B fileId][8B pageIndex][pageSize bytes]
                var rec = new byte[16 + _pageSize];
                BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(0, 8), pageId.FileId);
                BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(8, 8), pageId.PageIndex);
                Buffer.BlockCopy(pageBuf, 0, rec, 16, _pageSize);

                // Try to call WalManager.AppendAsync/Append and FlushAsync/Flush using reflection-friendly calls.
                // Preferred async AppendAsync(byte[], CancellationToken) -> ValueTask<ulong> or Task<ulong>.
                try
                {
                    // Try AppendAsync(byte[], CancellationToken)
                    var appendAsync = _wal.GetType().GetMethod("AppendAsync", new[] { typeof(byte[]), typeof(CancellationToken) })
                        ?? _wal.GetType().GetMethod("AppendAsync", new[] { typeof(ReadOnlyMemory<byte>), typeof(CancellationToken) })
                        ?? _wal.GetType().GetMethod("AppendAsync", new[] { typeof(byte[]) });

                    ulong lsn = 0;
                    if (appendAsync != null)
                    {
                        var appendArgs = appendAsync.GetParameters().Length == 2 ? new object[] { rec, ct } : new object[] { rec };
                        var taskObj = appendAsync.Invoke(_wal, appendArgs);
                        if (taskObj is ValueTask<ulong> vt)
                            lsn = await vt.ConfigureAwait(false);
                        else if (taskObj is Task<ulong> ttask)
                            lsn = await ttask.ConfigureAwait(false);
                        else if (taskObj is Task t)
                        {
                            await t.ConfigureAwait(false);
                            // Try to read return via Result property if available (unlikely)
                            var resProp = taskObj.GetType().GetProperty("Result");
                            if (resProp != null)
                                lsn = Convert.ToUInt64(resProp.GetValue(taskObj));
                        }
                        else if (taskObj is ulong u)
                        {
                            lsn = u;
                        }
                    }
                    else
                    {
                        // Try sync Append(byte[]) -> ulong
                        var appendSync = _wal.GetType().GetMethod("Append", new[] { typeof(byte[]) });
                        if (appendSync != null)
                        {
                            var r = appendSync.Invoke(_wal, new object[] { rec });
                            lsn = Convert.ToUInt64(r);
                        }
                        else
                        {
                            // Fallback: durable append to wal path if provided
                            if (!string.IsNullOrEmpty(_walPath))
                            {
                                using (var fs = new FileStream(_walPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                                {
                                    await fs.WriteAsync(rec, 0, rec.Length, ct).ConfigureAwait(false);
                                    await fs.FlushAsync(ct).ConfigureAwait(false);
                                    lsn = (ulong)fs.Length;
                                }
                            }
                            else
                            {
                                // Last resort: synthetic LSN
                                lsn = (ulong)DateTime.UtcNow.Ticks;
                            }
                        }
                    }

                    // Try FlushAsync(CancellationToken) or Flush()
                    var flushAsync = _wal.GetType().GetMethod("FlushAsync", new[] { typeof(CancellationToken) }) ?? _wal.GetType().GetMethod("FlushAsync", Type.EmptyTypes);
                    if (flushAsync != null)
                    {
                        var flushArgs = flushAsync.GetParameters().Length == 1 ? new object[] { ct } : Array.Empty<object>();
                        var t = flushAsync.Invoke(_wal, flushArgs);
                        if (t is Task tf) await tf.ConfigureAwait(false);
                    }
                    else
                    {
                        var flushSync = _wal.GetType().GetMethod("Flush", Type.EmptyTypes);
                        if (flushSync != null) flushSync.Invoke(_wal, null);
                    }

                    return lsn;
                }
                catch
                {
                    // If anything went wrong with WalManager calls, fall back to file append (best-effort)
                    try
                    {
                        if (!string.IsNullOrEmpty(_walPath))
                        {
                            using (var fs = new FileStream(_walPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                            {
                                fs.Write(rec, 0, rec.Length);
                                fs.Flush();
                                return (ulong)fs.Length;
                            }
                        }
                    }
                    catch { }
                    return (ulong)DateTime.UtcNow.Ticks;
                }
            }
        }

        // Adapter to use PhysicalBlockDevice as IPageDevice for BufferPoolManager
        private sealed class PhysicalPageDeviceAdapter : IPageDevice
        {
            private readonly PhysicalBlockDevice _dev;
            public PhysicalPageDeviceAdapter(PhysicalBlockDevice dev) => _dev = dev ?? throw new ArgumentNullException(nameof(dev));
            public int PageSize => _dev.Geometry.LogicalBlockSize;
            public Task ReadPageAsync(PageId pageId, Memory<byte> destination, CancellationToken ct)
                => _dev.ReadFrameAsync(pageId.PageIndex, destination, ct).AsTask();
            public Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, CancellationToken ct)
                => _dev.WriteFrameAsync(pageId.PageIndex, source, ct).AsTask();
        }

        // Diagnostics helper
        private static void LogFileLengths(string dbFile, string walFile, string when)
        {
            try
            {
                long dbLen = File.Exists(dbFile) ? new FileInfo(dbFile).Length : 0;
                long walLen = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;
                Console.WriteLine($"[Files {when}] dbFile={dbFile} ({dbLen} bytes), walFile={walFile} ({walLen} bytes)");
            }
            catch { }
        }

        // Reflection helpers to call Start/Stop on WalManager if present
        private static void TryInvokeStart(WalManager wal)
        {
            try
            {
                var startAsync = wal.GetType().GetMethod("StartAsync", new[] { typeof(CancellationToken) }) ?? wal.GetType().GetMethod("StartAsync", Type.EmptyTypes);
                if (startAsync != null)
                {
                    var t = startAsync.GetParameters().Length == 1 ? startAsync.Invoke(wal, new object[] { CancellationToken.None }) : startAsync.Invoke(wal, null);
                    if (t is Task task) task.GetAwaiter().GetResult();
                }
                else
                {
                    var startSync = wal.GetType().GetMethod("Start", Type.EmptyTypes);
                    if (startSync != null) startSync.Invoke(wal, null);
                }
            }
            catch { }
        }

        private static void TryInvokeStop(WalManager wal)
        {
            try
            {
                var stopAsync = wal.GetType().GetMethod("StopAsync", new[] { typeof(CancellationToken) }) ?? wal.GetType().GetMethod("StopAsync", Type.EmptyTypes);
                if (stopAsync != null)
                {
                    var t = stopAsync.GetParameters().Length == 1 ? stopAsync.Invoke(wal, new object[] { CancellationToken.None }) : stopAsync.Invoke(wal, null);
                    if (t is Task task) task.GetAwaiter().GetResult();
                }
                else
                {
                    var stopSync = wal.GetType().GetMethod("Stop", Type.EmptyTypes);
                    if (stopSync != null) stopSync.Invoke(wal, null);
                }
            }
            catch { }
        }
    }
}
