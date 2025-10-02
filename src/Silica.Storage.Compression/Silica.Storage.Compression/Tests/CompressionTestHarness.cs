using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;
using Silica.Storage.Stack;
using Silica.Storage.Decorators;

namespace Silica.Storage.Compression.Tests
{
    /// <summary>
    /// Harness for exercising the Compression mini-driver in scenarios
    /// similar to StorageTestHarness.
    /// </summary>
    public static class CompressionTestHarness
    {
        public static async Task RunAsync()
        {
            // Ensure the compression driver is registered
            CompressionRegistration.Register();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancellation requested...");
            };

            string compressedPath = @"c:\temp\silica_compressed.dat";
            string uncompressedPath = @"c:\temp\silica_uncompressed.dat";

            // 1. Create both scenarios
            await RunScenarioAsync(true, compressedPath, cts.Token);
            await RunScenarioAsync(false, uncompressedPath, cts.Token);

            // 2. Reload and verify
            await ReloadAndVerifyAsync(compressedPath, true, cts.Token);
            await ReloadAndVerifyAsync(uncompressedPath, false, cts.Token);

            // 3. Negative cross-loads
            await ExpectMountFailureAsync(uncompressedPath, true, cts.Token);
            await ExpectMountFailureAsync(compressedPath, false, cts.Token);
        }

        private static async Task RunScenarioAsync(bool useCompression, string filePath, CancellationToken token)
        {
            Console.WriteLine();
            Console.WriteLine("=== {0} scenario (create) ===", useCompression ? "COMPRESSED" : "UNCOMPRESSED");

            if (File.Exists(filePath))
                File.Delete(filePath);

            var baseDevice = new Devices.PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = useCompression
                ? new[] { new MiniDriverSpec("compression") }
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

            var baseDevice = new Devices.PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = expectCompression
                ? new[] { new MiniDriverSpec("compression") }
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

            var baseDevice = new Devices.PhysicalBlockDevice(filePath);

            MiniDriverSpec[] specs = expectCompression
                ? new[] { new MiniDriverSpec("compression") }
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
