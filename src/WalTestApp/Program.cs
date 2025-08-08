using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Durability;
using SilicaDB.Metrics;

namespace SilicaDB.WalTester
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var tests = new (string Name, Func<Task> Action)[]
            {
                ("SequentialWriteRead", TestSequentialWriteRead),
                ("ConcurrentWrites",     TestConcurrentWrites),
                ("Cancellation",         TestCancellation),
                ("RestartPersistence",   TestRestartPersistence),
                ("InvalidUsage",         TestInvalidUsage),
            };

            int passed = 0, failed = 0;
            WriteBanner(" WAL MANAGER TEST SUITE ");

            foreach (var (name, action) in tests)
            {
                var sw = Stopwatch.StartNew();
                WriteTestHeader(name);
                try
                {
                    await action().ConfigureAwait(false);
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
            return failed == 0 ? 0 : -1;
        }

        // =====================================================================
        // Individual Test Cases
        // =====================================================================

        static async Task TestSequentialWriteRead()
        {
            var path = GetTempWal();
            var payloads = Enumerable.Range(1, 10)
                                     .Select(i => $"Record-{i}")
                                     .ToList();

            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(CancellationToken.None);
                foreach (var text in payloads)
                {
                    var data = Encoding.UTF8.GetBytes(text);
                    await wal.AppendAsync(new WalRecord(0, data), CancellationToken.None);
                }
                await wal.FlushAsync(CancellationToken.None);
                await wal.StopAsync(CancellationToken.None);
            }

            var read = await ReadAllPayloads(path);
            if (!read.SequenceEqual(payloads))
                throw new InvalidOperationException("Read-back payloads do not match the original sequence.");

            File.Delete(path);
        }

        static async Task TestConcurrentWrites()
        {
            var path = GetTempWal();
            const int writers = 4, perWriter = 100;
            var expected = new ConcurrentBag<string>();

            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(CancellationToken.None);

                var tasks = Enumerable.Range(0, writers).Select(id => Task.Run(async () =>
                {
                    for (int i = 1; i <= perWriter; i++)
                    {
                        var text = $"T{id}-{i}";
                        expected.Add(text);
                        var payload = Encoding.UTF8.GetBytes(text);
                        await wal.AppendAsync(new WalRecord(0, payload), CancellationToken.None);
                    }
                })).ToArray();

                await Task.WhenAll(tasks);
                await wal.FlushAsync(CancellationToken.None);
                await wal.StopAsync(CancellationToken.None);
            }

            var actual = await ReadAllPayloads(path);
            File.Delete(path);

            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            if (missing.Any() || extra.Any())
                throw new InvalidOperationException(
                    $"Mismatch in concurrent writes: missing {missing.Count}, extra {extra.Count}.");
        }

        static async Task TestCancellation()
        {
            var path = GetTempWal();
            var cts = new CancellationTokenSource(50);

            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(cts.Token);

                int count = 0;
                try
                {
                    while (true)
                    {
                        var text = $"X-{count++}";
                        var data = Encoding.UTF8.GetBytes(text);
                        await wal.AppendAsync(new WalRecord(0, data), cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // expected cancellation
                }

                if (count == 0)
                    throw new InvalidOperationException("No records were appended before cancellation.");

                await wal.StopAsync(CancellationToken.None);
            }

            var read = await ReadAllPayloads(path);
            if (read.Count < 1)
                throw new InvalidOperationException("No valid records persisted after cancellation.");

            File.Delete(path);
        }

        static async Task TestRestartPersistence()
        {
            var path = GetTempWal();
            var first = Enumerable.Range(1, 5).Select(i => $"A-{i}").ToList();
            var second = Enumerable.Range(1, 5).Select(i => $"B-{i}").ToList();

            // First session
            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(CancellationToken.None);
                foreach (var t in first)
                    await wal.AppendAsync(new WalRecord(0, Encoding.UTF8.GetBytes(t)), CancellationToken.None);
                await wal.FlushAsync(CancellationToken.None);
                await wal.StopAsync(CancellationToken.None);
            }

            // Second session
            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(CancellationToken.None);
                foreach (var t in second)
                    await wal.AppendAsync(new WalRecord(0, Encoding.UTF8.GetBytes(t)), CancellationToken.None);
                await wal.FlushAsync(CancellationToken.None);
                await wal.StopAsync(CancellationToken.None);
            }

            var all = await ReadAllPayloads(path);
            var expected = first.Concat(second).ToList();
            if (!all.SequenceEqual(expected))
                throw new InvalidOperationException("Payload sequence mismatch after restart.");

            File.Delete(path);
        }

        static async Task TestInvalidUsage()
        {
            var path = GetTempWal();
            var wal = new WalManager(path, new MetricsManager(), walName: null);

            // Append before StartAsync → should throw
            await AssertThrowsAsync<InvalidOperationException>(
                () => wal.AppendAsync(new WalRecord(0, Array.Empty<byte>()), CancellationToken.None)
            );

            // Stop before StartAsync → no exception
            await wal.StopAsync(CancellationToken.None);

            // Proper start & shutdown
            await wal.StartAsync(CancellationToken.None);
            await wal.StopAsync(CancellationToken.None);
            await wal.DisposeAsync();

            // Append after DisposeAsync → should throw
            await AssertThrowsAsync<ObjectDisposedException>(
                () => wal.AppendAsync(new WalRecord(0, Array.Empty<byte>()), CancellationToken.None)
            );

            File.Delete(path);
        }

        // =====================================================================
        // Helpers & I/O
        // =====================================================================

        static string GetTempWal()
            => Path.Combine(Path.GetTempPath(), $"silicadb_test_{Guid.NewGuid():N}.wal");

        static async Task<List<string>> ReadAllPayloads(string path)
        {
            var results = new List<string>();
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            var header = new byte[12];
            while (fs.Position < fs.Length)
            {
                int hr = await fs.ReadAsync(header, 0, header.Length);
                if (hr < header.Length) throw new IOException("Truncated header.");
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));

                var buf = new byte[len];
                int pr = await fs.ReadAsync(buf, 0, len);
                if (pr != len) throw new IOException("Truncated payload.");

                results.Add(Encoding.UTF8.GetString(buf));
            }

            return results;
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

            throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}.");
        }

        // =====================================================================
        // Console Formatting
        // =====================================================================

        static void WriteBanner(string text)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine(new string('=', text.Length + 4));
            Console.WriteLine($"= {text} =");
            Console.WriteLine(new string('=', text.Length + 4));
            Console.ResetColor();
            Console.WriteLine();
        }

        static void WriteTestHeader(string testName)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"-- {testName} --");
            Console.ResetColor();
        }

        static void WriteResult(
            string result,
            ConsoleColor color,
            TimeSpan duration,
            Exception? ex = null)
        {
            Console.ForegroundColor = color;
            Console.Write($"  [{result}]");
            Console.ResetColor();
            Console.Write($"  Duration: {duration.TotalMilliseconds:N0}ms");

            if (ex != null)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    → {ex.GetType().Name}: {ex.Message}");
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
            Console.WriteLine(new string('=', 30));

            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.Green;
                Console.Write($" ALL TESTS PASSED ({passed}/{passed}) ");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.BackgroundColor = ConsoleColor.Red;
                Console.Write($" TESTS FAILED: {failed} failed, {passed} passed ");
            }

            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine(new string('=', 30));
        }
    }
}
