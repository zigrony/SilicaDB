using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Durability;

namespace SilicaDB.CheckpointTester
{
    // ------------------------------------------------------------------
    // Fake WAL implementation driving checkpoint manager without real I/O
    // ------------------------------------------------------------------
    class FakeWal : IWalManager
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

    // ------------------------------------------------------------------
    // Mini test‐runner with colored PASS/FAIL and final summary table
    // ------------------------------------------------------------------
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var tests = new List<(string Name, Func<Task> Action)>
            {
                (nameof(TestSequentialCheckpoints), TestSequentialCheckpoints),
                (nameof(TestOutOfOrderLSNs),      TestOutOfOrderLSNs),
                (nameof(TestDiskPersistence),     TestDiskPersistence),
                (nameof(TestConcurrentAppend),    TestConcurrentAppend),
                // ➞ later: TestCancellation, TestFailureInjection, etc.
            };

            var results = new List<(string Name, bool Passed, TimeSpan Duration, string Error)>();

            Console.WriteLine();
            foreach (var (name, action) in tests)
            {
                Console.WriteLine($"==== Running {name} ====");
                var sw = Stopwatch.StartNew();
                try
                {
                    await action();
                    sw.Stop();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[ PASS ] {name} ({sw.ElapsedMilliseconds} ms)\n");
                    results.Add((name, true, sw.Elapsed, ""));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ FAIL ] {name} ({sw.ElapsedMilliseconds} ms)  → {ex.Message}\n");
                    results.Add((name, false, sw.Elapsed, ex.Message));
                }
                finally
                {
                    Console.ResetColor();
                }
            }

            PrintSummary(results);
            return results.All(r => r.Passed) ? 0 : 1;
        }

        static void PrintSummary(
            List<(string Name, bool Passed, TimeSpan Duration, string Error)> results)
        {
            Console.WriteLine("==== Test Summary ====\n");
            Console.WriteLine("| Test Name                 | Status | Duration |");
            Console.WriteLine("|---------------------------|--------|----------|");
            foreach (var (name, passed, duration, _) in results)
            {
                var status = passed ? "PASS" : "FAIL";
                Console.WriteLine(
                    $"| {name,-25} | {status,-6} | {duration.TotalMilliseconds,7:F0}ms |");
            }
            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        // Helpers for directory setup & assertions
        // ------------------------------------------------------------------
        static string GetTestDir(string name) =>
            Path.Combine(Path.GetTempPath(), "cpTester", name);

        static void CleanDir(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }

        static void AssertEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual))
                throw new Exception($"Expected {expected}, got {actual}");
        }

        // ------------------------------------------------------------------
        // 1) Sequential LSNs → checkpoints at 1,2,3
        // ------------------------------------------------------------------
        static async Task TestSequentialCheckpoints()
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

        // ------------------------------------------------------------------
        // 2) Out‐of‐order LSN injection
        // ------------------------------------------------------------------
        static async Task TestOutOfOrderLSNs()
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

        // ------------------------------------------------------------------
        // 3) Ensure files land on disk and are discoverable
        // ------------------------------------------------------------------
        static async Task TestDiskPersistence()
        {
            var dir = GetTestDir(nameof(TestDiskPersistence));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            await wal.AppendAsync(new WalRecord(42, Array.Empty<byte>()), CancellationToken.None);
            await cp.WriteCheckpointAsync(CancellationToken.None);

            var filesExist = Directory.GetFiles(dir).Any();
            if (!filesExist)
                throw new Exception("No checkpoint files on disk");

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(42L, ckpt?.LastSequenceNumber ?? -1L);
        }

        // ------------------------------------------------------------------
        // 4) Concurrent appends → single checkpoint
        // ------------------------------------------------------------------
        static async Task TestConcurrentAppend()
        {
            var dir = GetTestDir(nameof(TestConcurrentAppend));
            CleanDir(dir);

            await using var wal = new FakeWal();
            await using var cp = new CheckpointManager(dir, wal);
            await cp.StartAsync();

            var appendTasks = Enumerable.Range(1, 100)
                                        .Select(i => wal.AppendAsync(
                                            new WalRecord(0, Array.Empty<byte>()),
                                            CancellationToken.None));
            await Task.WhenAll(appendTasks);

            await cp.WriteCheckpointAsync(CancellationToken.None);

            var ckpt = await cp.ReadLatestCheckpointAsync(CancellationToken.None);
            AssertEqual(100L, ckpt?.LastSequenceNumber ?? -1L);
        }
    }
}
