using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Devices;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;
using Silica.Storage.Stack;
using Silica.Storage.Decorators;

namespace Silica.Storage.Tests
{
    public static class StorageTestHarness
    {
        public static async Task RunAsync()
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancellation requested...");
            };

            string uncompressedPath = @"c:\temp\testdevice_uncompressed.dat";
            string compressedPath = @"c:\temp\testdevice_compressed.dat";

            // 1. Create both scenarios fresh
            await RunScenarioAsync(false, uncompressedPath, cts.Token);
            await RunScenarioAsync(true, compressedPath, cts.Token);

            // 2. Reload and verify each
            await ReloadAndVerifyAsync(uncompressedPath, false, cts.Token);
            await ReloadAndVerifyAsync(compressedPath, true, cts.Token);

            // 3. Negative tests: cross-load
            await ExpectMountFailureAsync(uncompressedPath, true, cts.Token);
            await ExpectMountFailureAsync(compressedPath, false, cts.Token);

            // 4. Now that everything is disposed, compare files
            CompareFiles(uncompressedPath, compressedPath);
        }

        private static async Task RunScenarioAsync(bool useCompression, string filePath, CancellationToken token)
        {
            Console.WriteLine();
            Console.WriteLine("=== {0} scenario (create) ===", useCompression ? "COMPRESSED" : "UNCOMPRESSED");

            if (File.Exists(filePath))
                File.Delete(filePath);

            var baseDevice = new PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = useCompression
                ? new[] { new MiniDriverSpec(MiniDriverKind.Compression, p1: 1, p2: 5) }
                : Array.Empty<MiniDriverSpec>();

            ulong uuidLo = 0x1122334455667788UL;
            ulong uuidHi = 0x99AABBCCDDEEFF00UL;

            var device = StorageStackBuilder.Build(
                baseDevice,
                specs,
                logicalBlockSize: baseDevice.Geometry.LogicalBlockSize,
                deviceUuidLo: uuidLo,
                deviceUuidHi: uuidHi);

            DumpStack(device);

            try
            {
                if (device is IMountableStorage m)
                    await m.MountAsync(token);

                await RunBasicReadWriteTests(device, token);
            }
            finally
            {
                await DisposeDeviceChainAsync(device);
            }

            Console.WriteLine("=== {0} scenario complete ===", useCompression ? "COMPRESSED" : "UNCOMPRESSED");
        }

        private static async Task ReloadAndVerifyAsync(string filePath, bool expectCompression, CancellationToken token)
        {
            Console.WriteLine();
            Console.WriteLine("[Reload] {0} expecting {1}", filePath, expectCompression ? "Compression" : "No Compression");

            var baseDevice = new PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = expectCompression
                ? new[] { new MiniDriverSpec(MiniDriverKind.Compression, p1: 1, p2: 5) }
                : Array.Empty<MiniDriverSpec>();

            var device = StorageStackBuilder.Build(
                baseDevice,
                specs,
                logicalBlockSize: baseDevice.Geometry.LogicalBlockSize,
                deviceUuidLo: 0x1122334455667788UL,
                deviceUuidHi: 0x99AABBCCDDEEFF00UL);

            DumpStack(device);

            try
            {
                if (device is IMountableStorage m)
                    await m.MountAsync(token);

                await RunBasicReadWriteTests(device, token);
                Console.WriteLine("[Reload] Verification OK");
            }
            finally
            {
                await DisposeDeviceChainAsync(device);
            }
        }

        private static async Task ExpectMountFailureAsync(string filePath, bool expectCompression, CancellationToken token)
        {
            Console.WriteLine();
            Console.WriteLine("[Negative Test] Trying to load {0} as {1}", filePath, expectCompression ? "Compressed" : "Uncompressed");

            var baseDevice = new PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = expectCompression
                ? new[] { new MiniDriverSpec(MiniDriverKind.Compression, p1: 1, p2: 5) }
                : Array.Empty<MiniDriverSpec>();

            var device = StorageStackBuilder.Build(
                baseDevice,
                specs,
                logicalBlockSize: baseDevice.Geometry.LogicalBlockSize,
                deviceUuidLo: 0x1122334455667788UL,
                deviceUuidHi: 0x99AABBCCDDEEFF00UL);

            try
            {
                if (device is IMountableStorage m)
                    await m.MountAsync(token);

                Console.WriteLine("[Negative Test] ERROR: Mount succeeded unexpectedly!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Negative Test] Expected failure: " + ex.GetType().Name + " - " + ex.Message);
            }
            finally
            {
                await DisposeDeviceChainAsync(device);
            }
        }

        private static void CompareFiles(string path1, string path2)
        {
            Console.WriteLine();
            Console.WriteLine("[Compare] {0} vs {1}", Path.GetFileName(path1), Path.GetFileName(path2));

            var bytes1 = File.ReadAllBytes(path1);
            var bytes2 = File.ReadAllBytes(path2);

            // Full-file comparison
            bool equalFull = true;
            if (bytes1.Length != bytes2.Length)
            {
                equalFull = false;
            }
            else
            {
                for (int i = 0; i < bytes1.Length; i++)
                {
                    if (bytes1[i] != bytes2[i]) { equalFull = false; break; }
                }
            }

            // Payload-only comparison (ignore metadata frame 0)
            bool equalPayload = false;
            int block = 4096; // metadata frame size; matches default LBS in these scenarios
            if (bytes1.Length >= block && bytes2.Length >= block)
            {
                int len1 = bytes1.Length - block;
                int len2 = bytes2.Length - block;
                equalPayload = (len1 == len2);
                if (equalPayload)
                {
                    for (int i = 0; i < len1; i++)
                    {
                        if (bytes1[block + i] != bytes2[block + i]) { equalPayload = false; break; }
                    }
                }
            }

            if (equalFull)
            {
                Console.WriteLine("[Compare] Files are IDENTICAL (including metadata)");
            }
            else if (equalPayload)
            {
                Console.WriteLine("[Compare] Payload frames are IDENTICAL (metadata differs as expected)");
            }
            else
            {
                Console.WriteLine("[Compare] Files differ (lengths: {0} vs {1}); payload differs too", bytes1.Length, bytes2.Length);
            }
        }

        private static void DumpStack(IStorageDevice device)
        {
            IStorageDevice current = device;
            while (current != null)
            {
                Console.WriteLine(" - " + current.GetType().Name);
                if (current is StorageDeviceDecorator dec)
                {
                    current = dec.InnerDevice;
                }
                else
                {
                    current = null;
                }
            }
        }

        private static async Task RunBasicReadWriteTests(IStorageDevice device, CancellationToken token)
        {
            var frameSize = device.Geometry.LogicalBlockSize;
            var writeBuffer = new byte[frameSize];
            var readBuffer = new byte[frameSize];

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
        }

        /// <summary>
        /// Ensures every device in the stack is unmounted and disposed, including inner devices.
        /// </summary>
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
                {
                    inner = dec.InnerDevice;
                }

                try { await device.DisposeAsync(); }
                catch { /* ignore */ }

                device = inner;
            }
        }
    }
}
