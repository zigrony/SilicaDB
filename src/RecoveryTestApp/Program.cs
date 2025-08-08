using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Durability;
using SilicaDB.Metrics;

namespace SilicaDB.RecoveryTester
{
    internal class Program
    {
        private static int _passed, _failed;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== SilicaDB RecoverManager Tester ===\n");

            await Run("InvalidReadBeforeStart", TestInvalidReadBeforeStart);
            await Run("SequentialRead", TestSequentialRead);
            await Run("ReadAfterStopThrows", TestReadAfterStopThrows);
            await Run("DisposePreventsStart", TestDisposePreventsStart);
            await Run("TruncatedHeaderThrows", TestTruncatedHeaderThrows);
            await Run("TruncatedPayloadThrows", TestTruncatedPayloadThrows);
            await Run("CancellationDuringRead", TestCancellationDuringRead);

            Console.WriteLine($"\n  Passed: {_passed}, Failed: {_failed}");
            Environment.Exit(_failed == 0 ? 0 : 1);
        }

        private static async Task Run(string name, Func<Task> test)
        {
            Console.Write($"-- {name,-30}");
            try
            {
                await test();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" PASS");
                _passed++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" FAIL -- {ex.GetType().Name}: {ex.Message}");
                _failed++;
            }
            finally
            {
                Console.ResetColor();
            }
        }

        // 1) ReadNextAsync before StartAsync => InvalidOperationException
        private static async Task TestInvalidReadBeforeStart()
        {
            var path = TempWalPath();
            await using (var rec = new RecoverManager(path))
            {
                await AssertThrowsAsync<InvalidOperationException>(
                    () => rec.ReadNextAsync(CancellationToken.None));
            }
            File.Delete(path);
        }

        // 2) Sequential payloads round-trip via WalManager → RecoverManager
        private static async Task TestSequentialRead()
        {
            var path = TempWalPath();
            var expected = new[] { "One", "Two", "Three" };

            // write them
            await using (var wal = new WalManager(path, new MetricsManager(), walName: null))
            {
                await wal.StartAsync(default);
                foreach (var s in expected)
                    await wal.AppendAsync(new WalRecord(0, Encoding.UTF8.GetBytes(s)), default);
                await wal.FlushAsync(default);
                await wal.StopAsync(default);
            }

            // read them back
            var actual = new List<string>();
            await using (var rec = new RecoverManager(path))
            {
                await rec.StartAsync(default);

                while (true)
                {
                    var wr = await rec.ReadNextAsync(default);
                    if (wr is null) break;
                    actual.Add(Encoding.UTF8.GetString(wr.Payload.Span));
                }

                await rec.StopAsync(default);
            }

            if (!expected.SequenceEqual(actual))
                throw new Exception($"Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
            File.Delete(path);
        }

        // 3) After StopAsync, ReadNextAsync throws InvalidOperationException
        private static async Task TestReadAfterStopThrows()
        {
            var path = TempWalPath();
            File.WriteAllBytes(path, Array.Empty<byte>());

            // we don’t use 'await using' because we need to call StopAsync & DisposeAsync
            var rec = new RecoverManager(path);
            try
            {
                await rec.StartAsync(default);
                await rec.StopAsync(default);

                await AssertThrowsAsync<InvalidOperationException>(
                    () => rec.ReadNextAsync(default));
            }
            finally
            {
                await rec.DisposeAsync();
            }

            File.Delete(path);
        }

        // 4) DisposeAsync before StartAsync blocks Start/Read
        private static async Task TestDisposePreventsStart()
        {
            var path = TempWalPath();
            var rec = new RecoverManager(path);
            await rec.DisposeAsync();

            await AssertThrowsAsync<ObjectDisposedException>(
                () => rec.StartAsync(default));

            await AssertThrowsAsync<ObjectDisposedException>(
                () => rec.ReadNextAsync(default));
            File.Delete(path);
        }

        // 5) Truncated header (<12 bytes) => IOException on ReadNextAsync
        private static async Task TestTruncatedHeaderThrows()
        {
            var path = TempWalPath();
            File.WriteAllBytes(path, new byte[8]);

            var rec = new RecoverManager(path);
            try
            {
                await rec.StartAsync(default);
                await AssertThrowsAsync<IOException>(
                    () => rec.ReadNextAsync(default));
            }
            finally
            {
                await rec.DisposeAsync();
            }

            File.Delete(path);
        }

        // 6) Header ok, payload too short => IOException
        private static async Task TestTruncatedPayloadThrows()
        {
            var path = TempWalPath();
            var header = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(header, 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 10);
            File.WriteAllBytes(path, header.Concat(new byte[5]).ToArray());

            var rec = new RecoverManager(path);
            try
            {
                await rec.StartAsync(default);
                var ex = await AssertThrowsAsync<IOException>(
                    () => rec.ReadNextAsync(default));

                if (!ex.Message.Contains("Truncated WAL payload"))
                    throw new Exception($"Unexpected message: {ex.Message}");
            }
            finally
            {
                await rec.DisposeAsync();
            }

            File.Delete(path);
        }

        // 7) A canceled token immediately aborts ReadNextAsync
        private static async Task TestCancellationDuringRead()
        {
            var path = TempWalPath();
            var header = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(header, 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1000);
            File.WriteAllBytes(path, header);

            var rec = new RecoverManager(path);
            try
            {
                await rec.StartAsync(default);

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertThrowsAsync<OperationCanceledException>(
                    () => rec.ReadNextAsync(cts.Token));
            }
            finally
            {
                await rec.DisposeAsync();
            }

            File.Delete(path);
        }

        // Utility
        private static string TempWalPath()
            => Path.Combine(Path.GetTempPath(), $"silicadb_recovery_{Guid.NewGuid():N}.wal");

        private static async Task<T> AssertThrowsAsync<T>(Func<Task> act)
            where T : Exception
        {
            try
            {
                await act();
            }
            catch (T ex)
            {
                return ex;
            }

            throw new Exception($"Expected {typeof(T).Name}");
        }
    }
}
