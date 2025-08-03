// File: Program.cs
// Namespace: SilicaDB.DeviceTester

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices;
using SilicaDB.Devices.Interfaces;

namespace SilicaDB.DeviceTester
{
    class Program
    {
        // fine-tune these for your cancellation test
        private const int CancellationFloodCount = 500;
        private const int CancellationTimeoutMs = 20;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== SilicaDB Device Tester ===");

            await TestDeviceAsync(new InMemoryDevice(),
                                  "InMemoryDevice");
            await TestDeviceAsync(
                new StreamDevice(
                    new MemoryStream(AsyncStorageDeviceBase.FrameSize * 4),
                    true),
                "StreamDevice (MemoryStream)");

            var tempPath = Path.Combine(
                Path.GetTempPath(), "silicadb_test.bin");
            if (File.Exists(tempPath)) File.Delete(tempPath);

            await TestDeviceAsync(
                new PhysicalBlockDevice(tempPath),
                $"PhysicalBlockDevice ({tempPath})");

            Console.WriteLine("\n=== All tests completed ===");
        }

        static async Task TestDeviceAsync(
            IStorageDevice device, string name)
        {
            Console.WriteLine($"\n--- {name} ---");

            await device.MountAsync();
            Console.WriteLine("  [OK] Mounted");

            await RunTestAsync("Basic read/write",
                               () => BasicReadWriteTest(device));
            await RunTestAsync("Randomized stress",
                               () => RandomizedStressTest(device));

            if (device is PhysicalBlockDevice)
                await RunTestAsync(
                    $"Cancellation/timeout ({CancellationFloodCount} writes, {CancellationTimeoutMs}ms)",
                    () => CancellationTest(device));
            else
                RunSkipTest("Cancellation/timeout");

            await RunTestAsync("Mount/unmount under load",
                               () => MountUnmountUnderLoadTest(device));
            await RunTestAsync(
                "Read-after-unmount throws",
                () => AssertThrowsAsync<InvalidOperationException>(
                    () => device.ReadFrameAsync(0)));
            await RunTestAsync("Dispose is idempotent",
                               () => device.DisposeAsync().AsTask());
        }

        static async Task RunTestAsync(
            string testName, Func<Task> testFunc)
        {
            Console.Write($"  • {testName,-45}");
            var sw = Stopwatch.StartNew();
            try
            {
                await testFunc();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(" [PASS]");
                Console.ResetColor();
                Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($" [FAIL] {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
            }
        }

        static void RunSkipTest(string testName)
        {
            Console.Write($"  • {testName,-45}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [SKIPPED]");
            Console.ResetColor();
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        static async Task AssertThrowsAsync<TEx>(Func<Task> action)
            where TEx : Exception
        {
            try
            {
                await action();
                throw new Exception($"Expected {typeof(TEx).Name}");
            }
            catch (TEx)
            {
                // OK
            }
        }

        // 1) Basic I/O
        static async Task BasicReadWriteTest(IStorageDevice device)
        {
            var buf = new byte[AsyncStorageDeviceBase.FrameSize];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (byte)(i % 256);

            await device.WriteFrameAsync(0, buf);
            var r0 = await device.ReadFrameAsync(0);
            Assert(r0.SequenceEqual(buf), "frame 0 mismatch");

            var empty = await device.ReadFrameAsync(10);
            Assert(empty.All(b => b == 0), "nonzero in empty frame");
        }

        // 2) Randomized stress
        static async Task RandomizedStressTest(IStorageDevice device)
        {
            var rnd = new Random(123);
            const int N = 1000;
            const long hammer = 0;

            var tasks = Enumerable.Range(0, N).Select(async i =>
            {
                if (rnd.NextDouble() < 0.1)
                {
                    var tmp = new byte[AsyncStorageDeviceBase.FrameSize];
                    rnd.NextBytes(tmp);
                    await device.WriteFrameAsync(hammer, tmp);
                }
                else
                {
                    long fid = i + 1;
                    var tmp = new byte[AsyncStorageDeviceBase.FrameSize];
                    for (int j = 0; j < tmp.Length; j++)
                        tmp[j] = (byte)((fid + j) % 256);

                    await device.WriteFrameAsync(fid, tmp);
                    var ret = await device.ReadFrameAsync(fid);
                    Assert(ret.SequenceEqual(tmp), $"fid {fid} mismatch");
                }
            });

            await Task.WhenAll(tasks);
        }

        // 3) Cancellation/timeout stress
        static async Task CancellationTest(IStorageDevice device)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(CancellationTimeoutMs);

            var attempts = CancellationFloodCount;
            var flood = Enumerable.Range(0, attempts)
                .Select(_ => device.WriteFrameAsync(
                    1,
                    new byte[AsyncStorageDeviceBase.FrameSize],
                    cts.Token));

            await AssertThrowsAsync<OperationCanceledException>(
                () => Task.WhenAll(flood));
        }

        // 4) Mount/unmount under load
        static async Task MountUnmountUnderLoadTest(IStorageDevice device)
        {
            using var cts = new CancellationTokenSource();
            var worker = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        await device.ReadFrameAsync(0);
                        await Task.Delay(10, cts.Token);
                    }
                }
                catch
                {
                    // expected when unmounted
                }
            });

            await Task.Delay(100);
            await device.UnmountAsync();
            cts.Cancel();
            await worker;
        }
    }
}
