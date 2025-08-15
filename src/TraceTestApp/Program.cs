using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SilicaDB.Diagnostics.Tracing;
using SilicaDB.Diagnostics.Tracing.Sinks;
using SilicaDB.Devices;
using SilicaDB.Evictions;

namespace TraceTester
{
    class Program
    {
        private static readonly BoundedTraceBuffer _buffer = new(capacity: 1024);
        private static readonly ConsoleTraceSink _console = new();

        static async Task Main(string[] args)
        {
            //––– Bootstrap tracing once –––
            Trace.RegisterSink(_buffer);
            Trace.RegisterSink(_console);
            Trace.EnableCategories(
                TraceCategory.Cache,
                TraceCategory.Device,
                TraceCategory.Locks
            );

            Console.WriteLine("=== FAST-PATH TEST ===");
            await FastPathTest();
            PrintTrace(TraceCategory.Cache);

            Console.WriteLine("\n=== SLOW-PATH FAIRNESS TEST ===");
            await SlowPathFairnessTest(5);
            PrintTrace(TraceCategory.Locks);

            Console.WriteLine("\n=== STORAGE WRITE TESTS ===");
            var devices = new (string Name, AsyncStorageDeviceBase Dev)[]
            {
                ("InMemoryDevice",      new InMemoryDevice()),
                ("PhysicalBlockDevice", new PhysicalBlockDevice(@"C:\temp\Test.sdb")),
                ("StreamDevice",        new StreamDevice(new MemoryStream()))
            };

            foreach (var (name, dev) in devices)
            {
                Console.WriteLine($"\n--- {name} ---");
                await StorageWriteTest(name, dev);
                PrintTrace(TraceCategory.Device);
            }

            Console.WriteLine("\nAll tests done. Press any key to exit.");
            Console.ReadKey();
        }

        // FAST-PATH: uncontended lock
        static async Task FastPathTest()
        {
            var latch = new AsyncLock();
            using (await latch.LockAsync()) { }
            Console.WriteLine("  Fast-path lock acquired and released.");
        }

        // SLOW-PATH FAIRNESS: strict FIFO
        // Refactored Slow-Path FIFO Fairness Test
        static async Task SlowPathFairnessTest(int count)
        {
            var latch = new AsyncLock();

            // 1) Hold the lock so subsequent calls queue
            using var holder = await latch.LockAsync();

            var intents = new List<int>();
            var grants = new List<int>();
            var tasks = new List<Task>();

            // 2) Enqueue waiters one-by-one
            for (int i = 1; i <= count; i++)
            {
                // capture loop index in its own local
                int id = i;

                // start the async acquire without awaiting
                var lockTask = latch.LockAsync();

                // force the lock's queue logic to run immediately
                await Task.Yield();

                // record enqueue intent
                Trace.Info(TraceCategory.Locks,
                           src: "TestHarness.Enqueue",
                           res: id.ToString());
                intents.Add(id);

                // when the lock is granted, record Acquire, Release, and store id
                tasks.Add(Task.Run(async () =>
                {
                    using (await lockTask.ConfigureAwait(false))
                    {
                        Trace.Info(TraceCategory.Locks,
                                   src: "TestHarness.Acquire",
                                   res: id.ToString());
                    }

                    Trace.Info(TraceCategory.Locks,
                               src: "TestHarness.Release",
                               res: id.ToString());

                    lock (grants)
                        grants.Add(id);
                }));
            }

            // 3) Release holder so queued waiters can run
            holder.Dispose();
            await Task.WhenAll(tasks);

            Console.WriteLine("  Intent order: " + string.Join(", ", intents));
            Console.WriteLine("  Grant  order: " + string.Join(", ", grants));

            // 4) Assert strict FIFO
            if (!intents.SequenceEqual(grants))
            {
                throw new InvalidOperationException(
                    $"FIFO violation! intended=[{string.Join(",", intents)}], " +
                    $"granted=[{string.Join(",", grants)}]");
            }

            Console.WriteLine("  FIFO fairness VERIFIED.");
        }

        // STORAGE WRITE: mount, write, unmount
        static async Task StorageWriteTest(string name, AsyncStorageDeviceBase device)
        {
            Console.WriteLine($"---> MountAsync on {name}");
            await device.MountAsync();

            Console.WriteLine($"---> WriteFrameAsync on {name}");
            var payload = new byte[8192];
            new Random(42).NextBytes(payload);
            await device.WriteFrameAsync(42, payload);

            Console.WriteLine($"---> UnmountAsync on {name}");
            await device.UnmountAsync();

            Console.WriteLine($"  {name} write completed.");
        }

        // Print all events for a given category
        static void PrintTrace(TraceCategory category)
        {
            var events = _buffer
                .Snapshot(maxCount: 1024)
                .Where(e => e.Category == category)
                .OrderBy(e => e.Seq)
                .ToList();

            Console.WriteLine($"\n--- Traces for {category} ---");
            foreach (var e in events)
            {
                var dur = e.DurationMicros.HasValue
                    ? $" (+{e.DurationMicros.Value / 1000.0:F3} ms)"
                    : "";
                var res = string.IsNullOrEmpty(e.Resource) ? "" : $" res={e.Resource}";
                Console.WriteLine($"{e.Seq,4} {e.Type,-9} {e.Source,-24}{res}{dur}");
            }

            // clear out entries so next PrintTrace only shows new events
            _buffer.Clear();
        }

    }
}
