using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.Devices;
using Silica.Storage.Decorators;
using Silica.Storage.Stack;
using Silica.Storage.MiniDrivers;
using Silica.Storage.SqlMinidriver;

namespace Silica.Storage.SqlMinidriver.Tests
{
    public static class SqlMinidriverTestHarness
    {
        public static async Task RunAsync()
        {
            // Register the SQL minidriver so the builder can compose it.
            SqlRegistration.Register();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancellation requested...");
            };

            string path = Path.Combine(@"c:\temp\silica_sql_device.dat");

            // Clean previous file
            if (File.Exists(path))
                File.Delete(path);

            // Base device
            var baseDevice = new PhysicalBlockDevice(path);
            int lbs = baseDevice.Geometry.LogicalBlockSize;

            // SQL config metablob
            var sqlBlobs = new Dictionary<string, byte[]>
            {
                {
                    "sql.config",
                    Encoding.UTF8.GetBytes(
                        "{ \"PageSize\": 4096, \"ReservedSystemPages\": 4, \"AllocationMapInterval\": 256, \"InitialAllocationMaps\": 1 }"
                    )
                }
            };

            ulong uuidLo = 0x1122334455667788UL;
            ulong uuidHi = 0x99AABBCCDDEEFF00UL;

            // === FIRST MOUNT ===
            Console.WriteLine("=== FIRST MOUNT (format and write) ===");

            var specs = new[]
            {
                // IMPORTANT: use named argument 'blobs:' to avoid passing Dictionary to Param1 (uint)
                new MiniDriverSpec(kind: "sql", blobs: sqlBlobs)
            };

            var device1 = StorageStackBuilder.Build(
                baseDevice,
                specs,
                logicalBlockSize: lbs,
                deviceUuidLo: uuidLo,
                deviceUuidHi: uuidHi);

            DumpStack(device1);

            if (device1 is IMountableStorage m1)
                await m1.MountAsync(cts.Token);

            await RunBasicReadWriteTest(device1, cts.Token);

            // Unmount and dispose first stack
            await DisposeDeviceChainAsync(device1);

            // === SECOND MOUNT ===
            Console.WriteLine("=== SECOND MOUNT (load persisted manifest from frame 0) ===");

            // Read the persisted manifest directly from the file’s metadata frame (frame 0)
            var manifest = ReadOnDiskManifest(path, lbs);
            Console.WriteLine("On-disk manifest: LBS={0}, Entries={1}", manifest.LogicalBlockSize, manifest.Entries.Length);

            // Reconstruct specs from on-disk entries
            var specs2 = ConvertManifestToSpecs(manifest);

            // New base device over the same file
            var baseDevice2 = new PhysicalBlockDevice(path);

            var device2 = StorageStackBuilder.Build(
                baseDevice2,
                specs2,
                logicalBlockSize: manifest.LogicalBlockSize,
                deviceUuidLo: manifest.DeviceUuidLo,
                deviceUuidHi: manifest.DeviceUuidHi);

            DumpStack(device2);

            if (device2 is IMountableStorage m2)
                await m2.MountAsync(cts.Token);

            await RunBasicReadWriteTest(device2, cts.Token);

            Console.WriteLine("=== SQL minidriver two-phase test complete ===");

            await DisposeDeviceChainAsync(device2);
        }

        private static DeviceManifest ReadOnDiskManifest(string path, int logicalBlockSize)
        {
            // Read the first metadata frame (frame 0) directly from disk
            byte[] buf = new byte[logicalBlockSize];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int total = 0;
                while (total < buf.Length)
                {
                    int n = fs.Read(buf, total, buf.Length - total);
                    if (n == 0) break;
                    total += n;
                }
            }

            if (!DeviceManifest.TryDeserialize(buf, out var manifest))
                throw new InvalidOperationException("Failed to deserialize on-disk manifest from frame 0.");

            return manifest;
        }

        private static MiniDriverSpec[] ConvertManifestToSpecs(DeviceManifest manifest)
        {
            if (manifest.Entries is null || manifest.Entries.Length == 0)
                return Array.Empty<MiniDriverSpec>();

            var specs = new MiniDriverSpec[manifest.Entries.Length];
            for (int i = 0; i < manifest.Entries.Length; i++)
            {
                var e = manifest.Entries[i];
                // Preserve Param1-Param4 in case you use them later; include blobs.
                specs[i] = new MiniDriverSpec(
                    kind: e.Kind,
                    p1: e.Param1,
                    p2: e.Param2,
                    p3: e.Param3,
                    p4: e.Param4,
                    passive: false,
                    blobs: e.MetadataBlobs
                );
            }
            return specs;
        }

        private static void DumpStack(IStorageDevice device)
        {
            IStorageDevice current = device;
            while (current != null)
            {
                Console.WriteLine(" - " + current.GetType().Name);
                if (current is StorageDeviceDecorator dec)
                    current = dec.InnerDevice;
                else
                    current = null;
            }
        }

        private static async Task RunBasicReadWriteTest(IStorageDevice device, CancellationToken token)
        {
            int frameSize = device.Geometry.LogicalBlockSize;
            var writeBuffer = new byte[frameSize];
            var readBuffer = new byte[frameSize];

            // Fill with a simple pattern
            for (int i = 0; i < frameSize; i++)
                writeBuffer[i] = (byte)(i & 0xFF);

            await device.WriteFrameAsync(0, writeBuffer, token);
            await device.ReadFrameAsync(0, readBuffer, token);

            for (int i = 0; i < frameSize; i++)
            {
                if (writeBuffer[i] != readBuffer[i])
                    throw new InvalidOperationException(
                        $"Data mismatch at byte {i}: wrote {writeBuffer[i]}, read {readBuffer[i]}");
            }
            Console.WriteLine($"Round-trip OK, frame size {frameSize} bytes.");
        }

        private static async ValueTask DisposeDeviceChainAsync(IStorageDevice device)
        {
            while (device != null)
            {
                if (device is IMountableStorage m)
                {
                    try { await m.UnmountAsync(CancellationToken.None); }
                    catch { /* ignore */ }
                }

                IStorageDevice? inner = null;
                if (device is StorageDeviceDecorator dec)
                    inner = dec.InnerDevice;

                try { await device.DisposeAsync(); }
                catch { /* ignore */ }

                device = inner;
            }
        }
    }
}
