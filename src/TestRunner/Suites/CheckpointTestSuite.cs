using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Durability;
using SilicaDB.TestRunner;

namespace SilicaDB.TestRunner.Suites
{
    public class CheckpointTestSuite : ITestSuite
    {
        public string Name => "Checkpoint";

        public async Task RunAsync(TestContext ctx)
        {
            var swTotal = Stopwatch.StartNew();
            var results = new List<(string Name, bool Passed, TimeSpan Duration, string Error)>();

            // 1️⃣  Exactly the same names your old Program.cs used
            var tests = new List<(string Name, Func<Task> Action)>
            {
                ("TestSequentialCheckpoints", TestSequentialCheckpoints),
                ("TestOutOfOrderLSNs",       TestOutOfOrderLSNs),
                ("TestDiskPersistence",      TestDiskPersistence),
                ("TestConcurrentAppend",     TestConcurrentAppend),
            };

            foreach (var (name, action) in tests)
            {
                ctx.WriteInfo($"==== Running {name} ====");
                var sw = Stopwatch.StartNew();

                try
                {
                    await action();
                    sw.Stop();
                    ctx.WritePass($"[ PASS ] {name} ({sw.ElapsedMilliseconds} ms)");
                    results.Add((name, true, sw.Elapsed, ""));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    ctx.WriteFail($"[ FAIL ] {name} ({sw.ElapsedMilliseconds} ms)  → {ex.Message}");
                    results.Add((name, false, sw.Elapsed, ex.Message));
                }

                ctx.WriteInfo(string.Empty);
            }

            // 2️⃣  Summary table
            ctx.WriteInfo("==== Test Summary ====\n");
            ctx.WriteInfo("| Test Name                 | Status | Duration |");
            ctx.WriteInfo("|---------------------------|--------|----------|");
            foreach (var (name, passed, duration, _) in results)
            {
                var status = passed ? "PASS" : "FAIL";
                ctx.WriteInfo($"| {name,-25} | {status,-6} | {duration.TotalMilliseconds,7:F0}ms |");
            }
            ctx.WriteInfo(string.Empty);

            swTotal.Stop();
            ctx.WriteInfo($"[Done] Total elapsed {swTotal.Elapsed.TotalMilliseconds:F3} ms.");
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers from your old Program.cs
        // ────────────────────────────────────────────────────────────────────
        private static string GetTestDir(string name) =>
            Path.Combine(Path.GetTempPath(), "cpTester", name);

        private static void CleanDir(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }

        private static void AssertEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual))
                throw new Exception($"Expected {expected}, got {actual}");
        }

        // ────────────────────────────────────────────────────────────────────
        // 1) Sequential LSNs → checkpoints at 1,2,3
        // ────────────────────────────────────────────────────────────────────
        private static async Task TestSequentialCheckpoints()
        {
            var dir = GetTestDir(nameof(TestSequentialCheckpoints));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            for (int i = 1; i <= 3; i++)
            {
                await wal.AppendAsync(new WalRecord(i, Array.Empty<byte>()), CancellationToken.None);
                await cp.WriteCheckpointAsync(CancellationToken.None);
            }

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(3L, ckpt?.LastSequenceNumber ?? -1L);
        }

        // ────────────────────────────────────────────────────────────────────
        // 2) Out‐of‐order LSN injection
        // ────────────────────────────────────────────────────────────────────
        private static async Task TestOutOfOrderLSNs()
        {
            var dir = GetTestDir(nameof(TestOutOfOrderLSNs));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            await wal.AppendAsync(new WalRecord(5, Array.Empty<byte>()), CancellationToken.None);
            await cp.WriteCheckpointAsync(CancellationToken.None);

            await wal.AppendAsync(new WalRecord(4, Array.Empty<byte>()), CancellationToken.None);
            await cp.WriteCheckpointAsync(CancellationToken.None);

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(5L, ckpt?.LastSequenceNumber ?? -1L);
        }

        // ────────────────────────────────────────────────────────────────────
        // 3) Ensure files land on disk and are discoverable
        // ────────────────────────────────────────────────────────────────────
        private static async Task TestDiskPersistence()
        {
            var dir = GetTestDir(nameof(TestDiskPersistence));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            await wal.AppendAsync(new WalRecord(42, Array.Empty<byte>()), CancellationToken.None);
            await cp.WriteCheckpointAsync(CancellationToken.None);

            if (!Directory.GetFiles(dir).Any())
                throw new Exception("No checkpoint files on disk");

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(42L, ckpt?.LastSequenceNumber ?? -1L);
        }

        // ────────────────────────────────────────────────────────────────────
        // 4) Concurrent appends → single checkpoint
        // ────────────────────────────────────────────────────────────────────
        private static async Task TestConcurrentAppend()
        {
            var dir = GetTestDir(nameof(TestConcurrentAppend));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            var tasks = Enumerable.Range(1, 100)
                                  .Select(i => wal.AppendAsync(
                                      new WalRecord(0, Array.Empty<byte>()),
                                      CancellationToken.None));
            await Task.WhenAll(tasks);

            await cp.WriteCheckpointAsync(CancellationToken.None);

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(100L, ckpt?.LastSequenceNumber ?? -1L);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Fake WAL implementation (moved inside the suite)
        // ────────────────────────────────────────────────────────────────────
        private class FakeWal : IWalManager
        {
            private long _lsn;

            public Task StartAsync(CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task AppendAsync(WalRecord record, CancellationToken cancellationToken)
            {
                _lsn = record.SequenceNumber > 0
                       ? record.SequenceNumber
                       : Interlocked.Increment(ref _lsn);
                return Task.CompletedTask;
            }

            public Task FlushAsync(CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;

            public void Dispose() { }

            public Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_lsn);
        }
    }
}
