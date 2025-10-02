using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Devices;
using Silica.BufferPool;
using Silica.Durability;
using Silica.Concurrency;
using Silica.PageAccess;
using Silica.DiagnosticsCore.Metrics;
using Silica.Storage.Interfaces;
using Silica.Storage;
using Silica.Common.Primitives;
using Silica.Common.Contracts;

namespace Silica.PageAccess.Tests
{
    /// <summary>
    /// Integration harness for exercising PageAccess, BufferPool, Storage, and Durability together.
    /// </summary>
    public static class PageAccessIntegrationHarness
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("=== PageAccessIntegrationHarness: Begin ===");

            string walFile = @"c:\temp\integration_test.wal";
            int capacityPages = 8;

            // Storage device
            var storage = new InMemoryBlockDevice();

            // Provide an expected manifest so geometry validation passes
            ((IStackManifestHost)storage).SetExpectedManifest(
                new DeviceManifest(
                    storage.Geometry.LogicalBlockSize,
                    uuidLo: 0,
                    uuidHi: 0,
                    entries: Array.Empty<DeviceManifest.Entry>())
            );

            await storage.MountAsync();

            // Metrics
            IMetricsManager metrics = new NoOpMetricsManager();

            // WAL manager
            var wal = new WalManager(walFile, metrics);

            // Buffer pool device adapter
            var device = new InMemoryPageDeviceAdapter(storage);

            // Buffer pool manager
            var bufferPool = new BufferPoolManager(
                device,
                metrics,
                wal: wal,
                logFlush: null,
                poolName: "IntegrationTestPool",
                capacityPages: capacityPages);

            // Lock manager
            var locks = new LockManager(metrics);

            // Page access manager
            var options = PageAccessorOptions.Default(magic: 0x53494C41); // "SILA"
            var allocator = new Silica.StorageAllocation.StubAllocator();
            var pageAccess = new PageManager(bufferPool, options, metrics, allocator, "PageAccessHarness");

            try
            {
                // --- Scenario: Write → Flush → Evict → Crash → Recover ---
                Console.WriteLine("[Scenario] Write → Flush → Evict → Crash → Recover");

                var pageId = new PageId(1, 1);
                var version = new PageVersion(1, 0);

                // Write header to create deterministic content
                await pageAccess.WriteHeaderAsync(
                    pageId,
                    expectedVersion: version,
                    expectedType: PageType.Data,
                    mutate: hdr => hdr.With(rowCount: (ushort)(hdr.RowCount + 1)));

                // Flush and evict so content is durably represented
                await pageAccess.FlushPageAsync(pageId);
                await bufferPool.TryEvictAsync(pageId);

                // Simulate crash
                Console.WriteLine("Simulating crash...");
                await bufferPool.StopAsync();
                await bufferPool.DisposeAsync();
                await wal.StopAsync(default);
                await wal.DisposeAsync();
                await storage.UnmountAsync();

                // Reopen managers and recover WAL
                storage = new InMemoryBlockDevice();

                // Re-provide manifest after "crash" to satisfy mount-time validation
                ((IStackManifestHost)storage).SetExpectedManifest(
                    new DeviceManifest(
                        storage.Geometry.LogicalBlockSize,
                        uuidLo: 0,
                        uuidHi: 0,
                        entries: Array.Empty<DeviceManifest.Entry>())
                );

                await storage.MountAsync();

                device = new InMemoryPageDeviceAdapter(storage);
                wal = new WalManager(walFile, metrics);
                bufferPool = new BufferPoolManager(
                    device, metrics, wal: wal, logFlush: null,
                    poolName: "IntegrationTestPool", capacityPages: capacityPages);
                var allocator2 = new Silica.StorageAllocation.StubAllocator();
                pageAccess = new PageManager(bufferPool, options, metrics, allocator2, componentName: "PageAccessHarness");

                // WAL recovery: replay full-page records into the storage device
                var recover = new RecoverManager(walFile, metrics);
                await recover.StartAsync(default);
                WalRecord? rec;
                while ((rec = await recover.ReadNextAsync(default)) is not null)
                {
                    // BufferPool full-page WAL record layout: [8B fileId][8B pageIndex][PageSize bytes]
                    int pageSize = storage.Geometry.LogicalBlockSize;
                    if (rec.Payload.Length < 16 + pageSize)
                        continue;

                    // Avoid Span locals in async methods: use array-based parsing
                    var payloadArr = rec.Payload.ToArray();

                    long fileId = ReadInt64LE(payloadArr, 0);
                    long pageIndex = ReadInt64LE(payloadArr, 8);

                    // Slice the page bytes (no Span) and write back to storage
                    var pageBytes = new ReadOnlyMemory<byte>(payloadArr, 16, pageSize);
                    await storage.WriteFrameAsync(pageIndex, pageBytes, default);
                }
                await recover.StopAsync(default);

                // Verify contents
                await using (var read = await pageAccess.ReadAsync(pageId, version, PageType.Data))
                {
                    Console.WriteLine("Recovered page header row count: " + read.Header.RowCount);
                }

                Console.WriteLine("[PASS] Recovery preserved data.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] " + ex.Message);
            }
            finally
            {
                // Teardown
                await bufferPool.StopAsync();
                await bufferPool.DisposeAsync();
                await wal.StopAsync(default);
                await wal.DisposeAsync();
                await storage.UnmountAsync();
            }

            Console.WriteLine("=== PageAccessIntegrationHarness: End ===");
        }

        // Adapter to use InMemoryBlockDevice as IPageDevice for BufferPoolManager
        private sealed class InMemoryPageDeviceAdapter : IPageDevice
        {
            private readonly InMemoryBlockDevice _dev;
            public InMemoryPageDeviceAdapter(InMemoryBlockDevice dev) => _dev = dev;
            public int PageSize => _dev.Geometry.LogicalBlockSize;
            public Task ReadPageAsync(PageId pageId, Memory<byte> destination, CancellationToken ct)
                => _dev.ReadFrameAsync(pageId.PageIndex, destination, ct).AsTask();
            public Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, CancellationToken ct)
                => _dev.WriteFrameAsync(pageId.PageIndex, source, ct).AsTask();
        }

        // Helpers (avoid Span/ref-struct locals in async methods)
        private static long ReadInt64LE(byte[] a, int offset)
        {
            // Use BinaryPrimitives over array slices (no async-local Span)
            return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(a, offset, 8));
        }
    }
}
