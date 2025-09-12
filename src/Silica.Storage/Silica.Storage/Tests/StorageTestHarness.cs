using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Silica.Storage;
using Silica.Storage.Devices;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Tests
{
    public static class StorageTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Silica.Storage Test Harness ===");

            // Test each core device type
            await TestDevice("InMemoryDevice", new InMemoryDevice());
            await TestDevice("LoopbackDevice", new LoopbackDevice(frameSize: 4096));
            await TestDevice("TelemetryDevice", new TelemetryDevice());
            await TestDevice("PhysicalBlockDevice",
                new PhysicalBlockDevice(Path.Combine(Path.GetTempPath(), "silica_test_device.bin")));
            await TestDevice("StreamDevice",
                new StreamDevice(new MemoryStream(new byte[4096 * 4]), ownsStream: true));

            // Wrappers
            await TestDevice("FaultInjectionDevice (wrap InMemory)",
                new FaultInjectionDevice(new InMemoryDevice(), faultRate: 0.2));
            await TestDevice("LatencyJitterDevice (wrap InMemory)",
                new LatencyJitterDevice(new InMemoryDevice(),
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5)));

            Console.WriteLine("=== All tests completed ===");
        }

        private static async Task TestDevice(string name, IStorageDevice device)
        {
            Console.WriteLine($"\n--- Testing {name} ---");

            var mountable = device as IMountableStorage;
            if (mountable != null)
                await mountable.MountAsync();

            try
            {
                var geom = device.Geometry;
                var frameSize = geom.LogicalBlockSize;
                var testData = Encoding.ASCII.GetBytes("HelloSilica").AsMemory();
                var padded = new byte[frameSize];
                testData.CopyTo(padded);

                // Frame write/read
                await device.WriteFrameAsync(0, padded);
                var readBuf = new byte[frameSize];
                await device.ReadFrameAsync(0, readBuf);
                Console.WriteLine($"Frame[0] read back: {Encoding.ASCII.GetString(readBuf, 0, testData.Length)}");

                // Offset write/read
                var offsetData = Encoding.ASCII.GetBytes("OffsetIO");
                var offsetBuf = new byte[offsetData.Length];
                await device.WriteAsync(0, offsetData);
                await device.ReadAsync(0, offsetBuf);
                Console.WriteLine($"Offset[0] read back: {Encoding.ASCII.GetString(offsetBuf)}");

                // Flush
                await device.FlushAsync();

                // Exception triggers
                try
                {
                    await device.ReadAsync(-1, new byte[1]);
                }
                catch (InvalidOffsetException)
                {
                    Console.WriteLine("Caught expected InvalidOffsetException");
                }

                try
                {
                    await device.ReadAsync(0, new byte[3]); // likely misaligned
                }
                catch (AlignmentRequiredException)
                {
                    Console.WriteLine("Caught expected AlignmentRequiredException");
                }
                catch (InvalidLengthException)
                {
                    Console.WriteLine("Caught expected InvalidLengthException");
                }

                try
                {
                    await device.ReadFrameAsync(999999, new byte[frameSize]);
                }
                catch (DeviceReadOutOfRangeException)
                {
                    Console.WriteLine("Caught expected DeviceReadOutOfRangeException");
                }
            }
            finally
            {
                if (mountable != null)
                    await mountable.UnmountAsync();
                await device.DisposeAsync();
            }
        }
    }
}
