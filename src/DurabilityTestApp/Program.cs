using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Durability;

namespace SilicaDB.DurabilityTester
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            WriteBanner(" SILICA DB DURABILITY TESTER ");

            var tests = new (string Name, Func<Task> Action)[]
            {
                ("CleanStart",            TestCleanStart),
                ("RestartSequence",       TestRestartSequence),
                ("GetLastSequenceNumber", TestGetLastSequenceNumber),
                ("InvalidUsage",          TestInvalidUsage),
                ("Cancellation",          TestCancellation),
                ("ConcurrentAppend",      TestConcurrentAppend),
                ("LargePayload",          TestLargePayload),
                ("ReadOnlyFile",          TestReadOnlyFile)
            };

            int passed = 0, failed = 0;
            foreach (var (name, action) in tests)
            {
                WriteTestHeader(name);
                var sw = Stopwatch.StartNew();
                try
                {
                    await action();
                    sw.Stop();
                    WriteResult("PASS", ConsoleColor.Green, sw.Elapsed);
                    passed++;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    WriteResult("FAIL", ConsoleColor.Red, sw.Elapsed, ex);
                    failed++;
                }
            }

            WriteSummary(passed, failed);
            return failed == 0 ? 0 : 1;
        }

        // --------------------------------------------------
        // Test 1: Fresh WAL yields exactly N records
        // --------------------------------------------------
        static async Task TestCleanStart()
        {
            var dir = PrepareTestDir("CleanStart");
            var walPath = Path.Combine(dir, "wal.log");
            const int count = 100;

            using var wal = new WalManager(walPath);
            await wal.StartAsync(default);
            for (int i = 1; i <= count; i++)
                await wal.AppendAsync(new WalRecord(0, BitConverter.GetBytes(i)), default);
            await wal.FlushAsync(default);
            await wal.StopAsync(default);

            int seen = await CountRecords(walPath);
            if (seen != count)
                throw new Exception($"Expected {count}, got {seen}");
        }

        // --------------------------------------------------
        // Test 2: Restart WAL, append more, recover total
        // --------------------------------------------------
        static async Task TestRestartSequence()
        {
            var dir = PrepareTestDir("RestartSequence");
            var walPath = Path.Combine(dir, "wal.log");
            const int first = 50;
            const int second = 30;

            // First session
            using (var wal = new WalManager(walPath))
            {
                await wal.StartAsync(default);
                for (int i = 1; i <= first; i++)
                    await wal.AppendAsync(new WalRecord(0, BitConverter.GetBytes(i)), default);
                await wal.FlushAsync(default);
                await wal.StopAsync(default);
            }

            // Second session
            using (var wal = new WalManager(walPath))
            {
                await wal.StartAsync(default);
                for (int i = first + 1; i <= first + second; i++)
                    await wal.AppendAsync(new WalRecord(0, BitConverter.GetBytes(i)), default);
                await wal.FlushAsync(default);
                await wal.StopAsync(default);
            }

            int seen = await CountRecords(walPath);
            if (seen != first + second)
                throw new Exception($"Expected {first + second}, got {seen}");
        }

        // --------------------------------------------------
        // Test 3: GetLastSequenceNumberAsync returns correct LSN
        // --------------------------------------------------
        static async Task TestGetLastSequenceNumber()
        {
            var dir = PrepareTestDir("GetLastLSN");
            var walPath = Path.Combine(dir, "wal.log");
            const int total = 20;

            using var wal = new WalManager(walPath);
            await wal.StartAsync(default);
            for (int i = 1; i <= total; i++)
                await wal.AppendAsync(new WalRecord(0, BitConverter.GetBytes(i)), default);

            long lsn = await wal.GetLastSequenceNumberAsync();
            await wal.StopAsync(default);

            if (lsn != total)
                throw new Exception($"Expected LSN {total}, got {lsn}");
        }

        // --------------------------------------------------
        // Test 4: Invalid usage before/after Start/Dispose
        // --------------------------------------------------
        static async Task TestInvalidUsage()
        {
            var dir = PrepareTestDir("InvalidUsage");
            var walPath = Path.Combine(dir, "wal.log");
            var wal = new WalManager(walPath);

            // Append before Start
            await AssertThrowsAsync<InvalidOperationException>(
                () => wal.AppendAsync(new WalRecord(0, Array.Empty<byte>()), default));

            // Stop without Start — no throw
            await wal.StopAsync(default);

            // Dispose
            await wal.DisposeAsync();

            // Append after Dispose
            await AssertThrowsAsync<ObjectDisposedException>(
                () => wal.AppendAsync(new WalRecord(0, Array.Empty<byte>()), default));
        }

        // --------------------------------------------------
        // Test 5: Cancellation aborts AppendAsync, persisted == appended
        // --------------------------------------------------
        static async Task TestCancellation()
        {
            var dir = PrepareTestDir("Cancellation");
            var walPath = Path.Combine(dir, "wal.log");
            using var cts = new CancellationTokenSource(50);

            int appended = 0;
            using (var wal = new WalManager(walPath))
            {
                await wal.StartAsync(cts.Token);
                try
                {
                    while (true)
                    {
                        await wal.AppendAsync(new WalRecord(0, new byte[8]), cts.Token);
                        appended++;
                    }
                }
                catch (OperationCanceledException) { }

                await wal.FlushAsync(default);
                await wal.StopAsync(default);
            }

            int seen = await CountRecords(walPath);
            if (seen != appended)
                throw new Exception($"Expected {appended}, got {seen}");
        }

        // --------------------------------------------------
        // Test 6: Concurrent appends stress-lock and recover all
        // --------------------------------------------------
        static async Task TestConcurrentAppend()
        {
            var dir = PrepareTestDir("ConcurrentAppend");
            var walPath = Path.Combine(dir, "wal.log");
            const int producers = 4, perProducer = 150;
            var expectedTotal = producers * perProducer;

            using var wal = new WalManager(walPath);
            await wal.StartAsync(default);
            var tasks = Enumerable.Range(0, producers).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    await wal.AppendAsync(new WalRecord(0, BitConverter.GetBytes(i)), default);
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            await wal.FlushAsync(default);
            await wal.StopAsync(default);

            int seen = await CountRecords(walPath);
            if (seen != expectedTotal)
                throw new Exception($"Expected {expectedTotal}, got {seen}");
        }

        // --------------------------------------------------
        // Test 7: Large payloads round-trip intact
        // --------------------------------------------------
        static async Task TestLargePayload()
        {
            var dir = PrepareTestDir("LargePayload");
            var walPath = Path.Combine(dir, "wal.log");
            int[] sizes = { 1_024, 8_192, 65_536 };

            // Prepare payloads
            var payloads = sizes.Select((sz, idx) =>
            {
                var buf = new byte[sz];
                for (int j = 0; j < sz; j++)
                    buf[j] = (byte)(idx + 1);
                return buf;
            }).ToArray();

            using var wal = new WalManager(walPath);
            await wal.StartAsync(default);
            foreach (var p in payloads)
                await wal.AppendAsync(new WalRecord(0, p), default);
            await wal.FlushAsync(default);
            await wal.StopAsync(default);

            // Recover and verify bytes
            int idx = 0;
            await using var rec = new RecoverManager(walPath);
            await rec.StartAsync(default);

            WalRecord? record;
            while ((record = await rec.ReadNextAsync(default)) != null)
            {
                var arr = record.Payload.ToArray();
                if (arr.Length != payloads[idx].Length ||
                    !arr.SequenceEqual(payloads[idx]))
                {
                    throw new Exception($"Payload mismatch at index {idx}");
                }
                idx++;
            }
            await rec.StopAsync(default);

            if (idx != payloads.Length)
                throw new Exception($"Expected {payloads.Length} records, got {idx}");
        }

        // --------------------------------------------------
        // Test 8: Opening WAL in read-only folder throws
        // --------------------------------------------------
        static async Task TestReadOnlyFile()
        {
            var dir = PrepareTestDir("ReadOnlyFile");
            var walPath = Path.Combine(dir, "wal.log");

            // Create empty WAL and mark read-only
            File.WriteAllBytes(walPath, Array.Empty<byte>());
            File.SetAttributes(walPath, FileAttributes.ReadOnly);

            try
            {
                var wal = new WalManager(walPath);
                await AssertThrowsAsync<UnauthorizedAccessException>(
                    () => wal.StartAsync(default));
            }
            finally
            {
                // Remove read-only so cleanup can delete
                File.SetAttributes(walPath, FileAttributes.Normal);
            }
        }

        // ==================================================
        // Helpers
        // ==================================================
        static string PrepareTestDir(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "SilicaDB_DurabilityTest", name);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            return dir;
        }

        static async Task<int> CountRecords(string path)
        {
            int count = 0;
            await using var rec = new RecoverManager(path);
            await rec.StartAsync(default);
            while (await rec.ReadNextAsync(default) != null)
                count++;
            await rec.StopAsync(default);
            return count;
        }

        static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action();
            }
            catch (T)
            {
                return;
            }
            throw new Exception($"Expected exception of type {typeof(T).Name}");
        }

        // ==================================================
        // Console formatting
        // ==================================================
        static void WriteBanner(string text)
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(new string('=', text.Length + 4));
            Console.WriteLine($"= {text} =");
            Console.WriteLine(new string('=', text.Length + 4));
            Console.ResetColor();
            Console.WriteLine();
        }

        static void WriteTestHeader(string name)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"-- {name} --");
            Console.ResetColor();
        }

        static void WriteResult(string result, ConsoleColor color, TimeSpan elapsed, Exception? ex = null)
        {
            Console.ForegroundColor = color;
            Console.Write($"  [{result}]");
            Console.ResetColor();
            Console.Write($" Duration: {elapsed.TotalMilliseconds:N0}ms");
            if (ex != null)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    ❌ {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine();
            }
        }

        static void WriteSummary(int passed, int failed)
        {
            Console.WriteLine();
            Console.WriteLine("============== SUMMARY ==============");
            Console.WriteLine($" Passed: {passed}    Failed: {failed}");
            Console.WriteLine("=====================================");
        }
    }
}
