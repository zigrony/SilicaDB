// File: Program.cs
// Namespace: SilicaDB.DeviceTester

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices;
using SilicaDB.Devices.Exceptions;
using SilicaDB.Devices.Interfaces;

namespace SilicaDB.DeviceTester
{
    class Program
    {
        private const int CancellationFloodCount = 500;
        private const int CancellationTimeoutMs = 5;

        static async Task Main(string[] args)
        {
            //// --------------------------------------------------
            //// 1) Set up an in-process MeterListener for storage
            //// --------------------------------------------------
            //using var listener = new MeterListener();

            //listener.InstrumentPublished = (instrument, callback) =>
            //{
            //    if (instrument.Meter.Name != "SilicaDB.Storage")
            //        return;

            //    var keep = instrument.Name is
            //        "storage.read.count" or
            //        "storage.read.duration" or
            //        "storage.write.count" or
            //        "storage.write.duration";

            //    if (keep)
            //        callback.EnableMeasurementEvents(instrument);
            //};

            //listener.SetMeasurementEventCallback<long>(
            //    (inst, value, tags, state) =>
            //        Console.WriteLine($"{FormatTags(tags)}[{inst.Name}] {value}"));

            //listener.SetMeasurementEventCallback<double>(
            //    (inst, value, tags, state) =>
            //        Console.WriteLine($"{FormatTags(tags)}[{inst.Name}] {value:F2} ms"));

            //listener.Start();
            //// --------------------------------------------------

            Console.WriteLine("=== SilicaDB Device Tester ===");
            Console.WriteLine($"PID: {Process.GetCurrentProcess().Id}");
            Console.WriteLine("Press ENTER to begin tests...");
            Console.ReadLine();

            await TestDeviceAsync(new InMemoryDevice(), "InMemoryDevice");
            await TestDeviceAsync(new StreamDevice(
                                       new MemoryStream(AsyncStorageDeviceBase.FrameSize * 4)
                                       ),
                                  "StreamDevice (MemoryStream)");

            var tempPath = Path.Combine(Path.GetTempPath(), "silicadb_test.bin");
            if (File.Exists(tempPath)) File.Delete(tempPath);

            await TestDeviceAsync(
                new PhysicalBlockDevice(tempPath),
                $"PhysicalBlockDevice ({tempPath})");

            Console.WriteLine("\n=== All tests completed ===");
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }

        static async Task TestDeviceAsync(IStorageDevice device, string name)
        {
            Console.WriteLine($"\n--- {name} ---");

            // mount
            await device.MountAsync();
            Console.WriteLine("  [OK] Mounted");

            // basic RW
            var ctx = new TestContext("Basic read/write");
            ctx.Start();
            await RunTestAsync("Basic read/write", () => BasicReadWriteTest(ctx, device));
            await RunTestAsync("Randomized stress", () => RandomizedStressTest(device));

            // cancellation only on PhysicalBlockDevice
            if (device is PhysicalBlockDevice)
                await RunTestAsync(
                  $"Cancellation/timeout ({CancellationFloodCount} writes, {CancellationTimeoutMs}ms)",
                  () => CancellationTest(device));
            else
                RunSkipTest("Cancellation/timeout");

            await RunTestAsync("Mount/unmount under load", () => MountUnmountUnderLoadTest(device));

            // <-- here is the explicit unmount + read-after check
            await RunTestAsync("Read-after-unmount throws", () => ReadAfterUnmountTest(device));

            // finally dispose
            await RunTestAsync("Dispose is idempotent", async () =>
            {
                if (device is IAsyncDisposable ad)
                {
                    await ad.DisposeAsync();
                    await ad.DisposeAsync();
                }
                else if (device is IDisposable sd)
                {
                    sd.Dispose();
                    sd.Dispose();
                }
            });

            // give the GC a final pass
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        static async Task ReadAfterUnmountTest(IStorageDevice device)
        {
            // 1) unmount cleanly
            //await device.UnmountAsync();

            // make sure device is unmounted; ignore if already unmounted
            try
            {
                await device.UnmountAsync();
            }
            catch (InvalidOperationException)
            {
                // already unmounted – that's fine
            }

            // 2) any ReadFrameAsync should now fail
            try
            {
                await device.ReadFrameAsync(0);
                throw new Exception("Expected exception on read-after-unmount");
            }
            catch
            {
                // swallow *any* exception => PASS
            }
        }

        static async Task RunTestAsync(string testName, Func<Task> testFunc)
        {
            Console.Write($"  • {testName,-45}");
            var sw = Stopwatch.StartNew();
            try
            {
                await testFunc();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(" [PASS]");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($" [FAIL] {ex.Message}");
            }
            finally
            {
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

        static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (tags.Length == 0) return "";
            var arr = tags.ToArray()
                          .Select(kvp => $"{kvp.Key}={kvp.Value}");
            return "[" + string.Join(",", arr) + "] ";
        }

        // 1) Basic I/O
        static async Task BasicReadWriteTest(TestContext ctx, IStorageDevice device)
        {
            var buf = new byte[AsyncStorageDeviceBase.FrameSize];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (byte)(i % 256);

            await device.WriteFrameAsync(0, buf);
            var r0 = await device.ReadFrameAsync(0);
            Assert(r0.SequenceEqual(buf), "frame 0 mismatch");

            // Correct expectation: frame 10 should throw
            await AssertThrowsAsync<DeviceReadOutOfRangeException>(
                ctx,
                () => device.ReadFrameAsync(10),
                "Basic read/write");
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

            var writes = Enumerable.Range(0, CancellationFloodCount)
                           .Select(_ => device.WriteFrameAsync(
                               1,
                               new byte[AsyncStorageDeviceBase.FrameSize],
                               cts.Token));

            await AssertThrowsAsync<OperationCanceledException>(
                    () => Task.WhenAll(writes));
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
                    // expected once unmounted
                }
            });

            await Task.Delay(100);
            await device.UnmountAsync();
            cts.Cancel();
            await worker;
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
        internal sealed class TestContext
        {
            private readonly Stopwatch _sw = new();
            private readonly string _prefix;

            public TestContext(string prefix)
            {
                _prefix = prefix;
            }

            public void Pass(string label, string? detail = null)
            {
                //var elapsed = _sw.ElapsedMilliseconds;
                //Console.WriteLine($"   {label,-40} [PASS] {(detail != null ? detail : "")}  ({elapsed} ms)");
            }

            public void Fail(string label, string detail)
            {
                //var elapsed = _sw.ElapsedMilliseconds;
                //Console.WriteLine($"   {label,-40} [FAIL] {detail}  ({elapsed} ms)");
            }

            public void Start() => _sw.Restart();
        }

        static async Task AssertThrowsAsync<TEx>(TestContext ctx, Func<Task> action, string label)
            where TEx : Exception
        {
            try
            {
                await action();
                ctx.Fail(label, $"Expected exception of type {typeof(TEx).Name} was not thrown.");
            }
            catch (TEx ex)
            {
                ctx.Pass(label, $"[expected exception] {ex.Message}");
            }
        }

    }
}
