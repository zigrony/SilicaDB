using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;
using Silica.Storage.Stack;
using Silica.Storage.Decorators;
using Silica.Storage.Devices;

namespace Silica.Storage.Compression.Tests
{
    /// <summary>
    /// Harness to validate compression round-trip on both in-memory and physical block devices.
    /// </summary>
    public static class CompressionRoundTripHarness
    {
        public static async Task RunAsync()
        {
            // Ensure compression driver is registered
            CompressionRegistration.Register();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancellation requested...");
            };

            string compressedPath = Path.Combine(Path.GetTempPath(), "silica_compressed.dat");
            string uncompressedPath = Path.Combine(Path.GetTempPath(), "silica_uncompressed.dat");

            // Physical device scenarios
            await RunScenarioAsync("PHYSICAL", true, compressedPath, cts.Token);
            await RunScenarioAsync("PHYSICAL", false, uncompressedPath, cts.Token);

            // In-memory device scenarios
            await RunScenarioAsync("INMEMORY", true, null, cts.Token);
            await RunScenarioAsync("INMEMORY", false, null, cts.Token);
        }

        private static async Task RunScenarioAsync(string deviceType, bool useCompression, string? filePath, CancellationToken token)
        {
            Console.WriteLine();
            Console.WriteLine("=== {0} {1} scenario ===", deviceType, useCompression ? "COMPRESSED" : "UNCOMPRESSED");

            IStorageDevice baseDevice;
            if (deviceType == "PHYSICAL")
            {
                if (filePath == null) throw new ArgumentNullException(nameof(filePath));
                if (File.Exists(filePath))
                    File.Delete(filePath);
                baseDevice = new PhysicalBlockDevice(filePath);
            }
            else
            {
                baseDevice = new InMemoryBlockDevice();
            }

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

                await RunBasicReadWriteTest(device, token);
                Console.WriteLine("=== {0} {1} scenario complete ===", deviceType, useCompression ? "COMPRESSED" : "UNCOMPRESSED");
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
            Console.WriteLine("Round-trip OK, frame size {0} bytes.", frameSize);
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
