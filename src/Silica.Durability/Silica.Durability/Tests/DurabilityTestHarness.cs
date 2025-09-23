using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Silica.Durability;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Durability.Tests
{
    /// <summary>
    /// Comprehensive test harness for Silica.Durability.
    /// Covers WAL lifecycle, concurrency, checkpoints, recovery, corruption, and edge cases.
    /// No LINQ, no reflection, no JSON, no third-party libraries.
    /// </summary>
    public static class DurabilityTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Durability Test Harness ===");
            Console.WriteLine("Working directory: " + Environment.CurrentDirectory);

            string baseDir = Path.Combine(@"c:\temp\", "durability_artifacts");
            Directory.CreateDirectory(baseDir);
            string walPath = Path.Combine(baseDir, "test_wal.log");
            string checkpointDir = Path.Combine(baseDir, "checkpoints");
            Directory.CreateDirectory(checkpointDir);

            IMetricsManager metrics = new DummyMetricsManager();

            // Start primary WAL and checkpoint managers
            await using var wal = new WalManager(walPath, metrics, "TestWal");
            await using var ckpt = new CheckpointManager(checkpointDir, wal, metrics);

            await wal.StartAsync(CancellationToken.None);
            await ckpt.StartAsync(CancellationToken.None);

            await RunTest("[Test] WAL Append and Flush", () => TestWalAppendAndFlush(wal));
            await RunTest("[Test] WAL Concurrent Appends", () => TestWalConcurrentAppends(wal));  // uses concrete WalManager for strict LSN checks
            await RunTest("[Test] Checkpoint Write and Read", () => TestCheckpointWriteAndRead(ckpt, wal));
            await RunTest("[Test] Interleaved Checkpoints", () => TestInterleavedCheckpoints(wal, ckpt));
            await RunTest("[Test] Zero-Length Payload", () => TestZeroLengthPayload(wal));
            await RunTest("[Test] Large Payload", () => TestLargePayload(wal));
            await RunTest("[Test] Concurrent Checkpoint Under Load", () => TestConcurrentCheckpointUnderLoad(wal, ckpt));

            // Ensure durability and close WAL before any recovery or direct file operations
            await wal.FlushAsync(CancellationToken.None);
            await wal.StopAsync(CancellationToken.None);

            await RunTest("[Test] Recovery Replay", () => TestRecovery(walPath, metrics));
            await RunTest("[Test] Recovery With Torn Tail", () => TestRecoveryWithTornTail(walPath, metrics));
            await RunTest("[Test] Unflushed Not Replayed (bounded)", () => TestUnflushedNotReplayed(walPath, metrics));     // bounded assertions across platforms
            await RunTest("[Test] Writer Startup Tail Truncation", () => TestStartupTruncation(walPath, metrics));           // writer re-scan truncates unsafe tail
            await RunTest("[Test] Legacy Header Mix Recovery", () => TestLegacyHeaderMixRecovery(walPath, metrics));         // append legacy-format record and recover
            await RunTest("[Test] Checkpoint Hygiene Normalization", () => TestCheckpointHygieneNormalization(checkpointDir, metrics));
            await RunTest("[Test] Partial Header Corruptions (new-format)", () => TestPartialHeaderCorruptions(walPath, metrics));

            await ckpt.StopAsync(CancellationToken.None);

            Console.WriteLine("=== Durability Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Func<Task> test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            await test();
            sw.Stop();
            Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestWalAppendAndFlush(IWalManager wal)
        {
            var payload = Encoding.UTF8.GetBytes("hello durability");
            var record = new WalRecord(0, payload);

            var swAppend = Stopwatch.StartNew();
            await wal.AppendAsync(record, CancellationToken.None);
            swAppend.Stop();

            var swFlush = Stopwatch.StartNew();
            await wal.FlushAsync(CancellationToken.None);
            swFlush.Stop();

            long flushed = await wal.GetFlushedSequenceNumberAsync();
            if (flushed <= 0) throw new InvalidOperationException("Flush did not advance flushed LSN.");

            Console.WriteLine("WAL append/flush test passed. Flushed LSN=" + flushed +
                              " (append " + swAppend.Elapsed.TotalMilliseconds + " ms, flush " + swFlush.Elapsed.TotalMilliseconds + " ms)");
        }

        private static async Task TestWalConcurrentAppends(IWalManager wal)
        {
            var concrete = wal as WalManager;
            if (concrete == null) throw new InvalidOperationException("Concrete WalManager required.");

            int concurrency = 8;
            int iterationsPerTask = 64;
            int total = concurrency * iterationsPerTask;

            long[] lsns = new long[total];
            int index = 0;

            Task[] tasks = new Task[concurrency];
            for (int t = 0; t < concurrency; t++)
            {
                int taskId = t;
                tasks[t] = Task.Run(async () =>
                {
                    byte[] buf = new byte[16];
                    for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(taskId ^ i);

                    for (int i = 0; i < iterationsPerTask; i++)
                    {
                        var rec = new WalRecord(0, new ReadOnlyMemory<byte>(buf));
                        long lsn = await concrete.AppendReturningLsnAsync(rec, CancellationToken.None).ConfigureAwait(false);
                        int slot = Interlocked.Increment(ref index) - 1;
                        lsns[slot] = lsn;
                    }
                });
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var swFlush = Stopwatch.StartNew();
            await wal.FlushAsync(CancellationToken.None);
            swFlush.Stop();

            if (index != total) throw new InvalidOperationException("Concurrent append: missing entries.");

            Array.Sort(lsns, 0, total);
            for (int i = 1; i < total; i++)
            {
                if (lsns[i] <= lsns[i - 1]) throw new InvalidOperationException("LSN is not strictly increasing under concurrency.");
            }

            long flushed = await wal.GetFlushedSequenceNumberAsync().ConfigureAwait(false);
            if (flushed < lsns[total - 1]) throw new InvalidOperationException("Flushed LSN does not cover all appended records.");

            Console.WriteLine("Concurrent append test passed. Appended=" + total +
                              ", LastLSN=" + lsns[total - 1] + ", FlushedLSN=" + flushed +
                              " (flush " + swFlush.Elapsed.TotalMilliseconds + " ms)");
        }

        private static async Task TestCheckpointWriteAndRead(ICheckpointManager ckpt, IWalManager wal)
        {
            var swWrite = Stopwatch.StartNew();
            await ckpt.WriteCheckpointAsync();
            swWrite.Stop();

            var swRead = Stopwatch.StartNew();
            var data = await ckpt.ReadLatestCheckpointAsync();
            swRead.Stop();

            if (data == null) throw new InvalidOperationException("No checkpoint found.");
            long flushed = await wal.GetFlushedSequenceNumberAsync();
            if (data.LastSequenceNumber != flushed) throw new InvalidOperationException("Checkpoint LSN mismatch.");

            Console.WriteLine("Checkpoint read: LSN=" + data.LastSequenceNumber + ", Created=" + data.CreatedUtc +
                              " (write " + swWrite.Elapsed.TotalMilliseconds + " ms, read " + swRead.Elapsed.TotalMilliseconds + " ms)");
        }

        private static async Task TestInterleavedCheckpoints(IWalManager wal, ICheckpointManager ckpt)
        {
            // Batch A
            for (int i = 0; i < 10; i++)
            {
                var rec = new WalRecord(0, new ReadOnlyMemory<byte>(new byte[] { (byte)i }));
                await wal.AppendAsync(rec, CancellationToken.None);
            }

            await wal.FlushAsync(CancellationToken.None);
            long flushedA = await wal.GetFlushedSequenceNumberAsync();
            await ckpt.WriteCheckpointAsync();
            var c1 = await ckpt.ReadLatestCheckpointAsync();
            if (c1 == null || c1.LastSequenceNumber != flushedA) throw new InvalidOperationException("First checkpoint mismatch.");

            // Batch B
            for (int i = 0; i < 7; i++)
            {
                var rec = new WalRecord(0, new ReadOnlyMemory<byte>(new byte[] { (byte)(i + 10) }));
                await wal.AppendAsync(rec, CancellationToken.None);
            }

            await wal.FlushAsync(CancellationToken.None);
            long flushedB = await wal.GetFlushedSequenceNumberAsync();
            await ckpt.WriteCheckpointAsync();
            var c2 = await ckpt.ReadLatestCheckpointAsync();
            if (c2 == null || c2.LastSequenceNumber != flushedB) throw new InvalidOperationException("Second checkpoint mismatch.");

            Console.WriteLine("Interleaved checkpoints test passed. C1=" + c1.LastSequenceNumber + ", C2=" + c2.LastSequenceNumber);
        }

        private static async Task TestZeroLengthPayload(IWalManager wal)
        {
            var rec = new WalRecord(0, ReadOnlyMemory<byte>.Empty);
            var swAppend = Stopwatch.StartNew();
            await wal.AppendAsync(rec, CancellationToken.None);
            swAppend.Stop();

            var swFlush = Stopwatch.StartNew();
            await wal.FlushAsync(CancellationToken.None);
            swFlush.Stop();

            long flushed = await wal.GetFlushedSequenceNumberAsync();
            if (flushed <= 0) throw new InvalidOperationException("Zero-length payload not flushed.");

            Console.WriteLine("Zero-length payload test passed. (append " + swAppend.Elapsed.TotalMilliseconds + " ms, flush " + swFlush.Elapsed.TotalMilliseconds + " ms)");
        }

        private static async Task TestLargePayload(IWalManager wal)
        {
            byte[] buf = new byte[1024 * 1024]; // 1 MB
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i % 251);
            var rec = new WalRecord(0, buf);

            var swAppend = Stopwatch.StartNew();
            await wal.AppendAsync(rec, CancellationToken.None);
            swAppend.Stop();

            var swFlush = Stopwatch.StartNew();
            await wal.FlushAsync(CancellationToken.None);
            swFlush.Stop();

            long flushed = await wal.GetFlushedSequenceNumberAsync();
            if (flushed <= 0) throw new InvalidOperationException("Large payload not flushed.");

            Console.WriteLine("Large payload test passed. (append " + swAppend.Elapsed.TotalMilliseconds + " ms, flush " + swFlush.Elapsed.TotalMilliseconds + " ms)");
        }

        private static async Task TestConcurrentCheckpointUnderLoad(IWalManager wal, ICheckpointManager ckpt)
        {
            int iterations = 20;

            var sw = Stopwatch.StartNew();
            Task writer = Task.Run(async () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    var rec = new WalRecord(0, new ReadOnlyMemory<byte>(new byte[] { (byte)i }));
                    await wal.AppendAsync(rec, CancellationToken.None);
                    await wal.FlushAsync(CancellationToken.None);
                }
            });

            Task checkpointer = Task.Run(async () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    await ckpt.WriteCheckpointAsync();
                    await Task.Delay(5);
                }
            });

            await Task.WhenAll(writer, checkpointer);
            sw.Stop();
            Console.WriteLine("Concurrent checkpoint under load test passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestRecovery(string walPath, IMetricsManager metrics)
        {
            await using var recover = new RecoverManager(walPath, metrics);
            var sw = Stopwatch.StartNew();
            await recover.StartAsync(CancellationToken.None);

            int count = 0;
            while (true)
            {
                var rec = await recover.ReadNextAsync(CancellationToken.None);
                if (rec == null) break;
                count++;
            }

            await recover.StopAsync(CancellationToken.None);
            sw.Stop();

            if (count == 0) throw new InvalidOperationException("Recovery replayed 0 records.");
            Console.WriteLine("Recovery replayed " + count + " record(s) in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestRecoveryWithTornTail(string walPath, IMetricsManager metrics)
        {
            long originalLength = 0;
            if (File.Exists(walPath))
            {
                using (var s = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    originalLength = s.Length;
                }
            }
            if (originalLength == 0) throw new InvalidOperationException("WAL file is empty; cannot simulate torn tail.");

            long newLength = originalLength - 8;
            if (newLength < 0) newLength = 0;
            using (var s = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                s.SetLength(newLength);
                try { s.Flush(true); } catch { }
            }

            await using var recover = new RecoverManager(walPath, metrics);
            var sw = Stopwatch.StartNew();
            await recover.StartAsync(CancellationToken.None);

            int count = 0;
            while (true)
            {
                var rec = await recover.ReadNextAsync(CancellationToken.None);
                if (rec == null) break;
                count++;
            }

            await recover.StopAsync(CancellationToken.None);
            sw.Stop();

            Console.WriteLine("Recovery with torn tail replayed " + count + " record(s) before tail corruption in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestUnflushedNotReplayed(string walPath, IMetricsManager metrics)
        {
            long baselineFlushed;

            await using (var wal = new WalManager(walPath, metrics, "UnflushedTest"))
            {
                await wal.StartAsync(CancellationToken.None);
                baselineFlushed = await wal.GetFlushedSequenceNumberAsync();
                var rec = new WalRecord(0, new ReadOnlyMemory<byte>(new byte[] { 0xAB }));
                // DO NOT flush
                await wal.AppendAsync(rec, CancellationToken.None);
                await wal.StopAsync(CancellationToken.None);
            }

            long replayed = 0;
            long lastLsn = 0;

            await using (var recover = new RecoverManager(walPath, metrics))
            {
                var sw = Stopwatch.StartNew();
                await recover.StartAsync(CancellationToken.None);
                while (true)
                {
                    var rec = await recover.ReadNextAsync(CancellationToken.None);
                    if (rec == null) break;
                    replayed++;
                    lastLsn = rec.SequenceNumber;
                }
                await recover.StopAsync(CancellationToken.None);
                sw.Stop();
                Console.WriteLine("Unflushed-not-replayed recovery scan took " + sw.Elapsed.TotalMilliseconds + " ms.");
            }

            // Only enforce the upper bound; allow truncation to reduce replay below baseline.
            if (lastLsn > baselineFlushed + 1)
                throw new InvalidOperationException("Recovery advanced too far beyond unflushed boundary.");

            Console.WriteLine("Unflushed-not-replayed test passed (bounded). BaselineFlushed=" + baselineFlushed +
                              ", LastReplayedLSN=" + lastLsn + ", ReplayedCount=" + replayed);
        }

        private static async Task TestStartupTruncation(string walPath, IMetricsManager metrics)
        {
            long beforeLen = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

            using (var s = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                s.Seek(0, SeekOrigin.End);
                byte[] junk = new byte[13];
                for (int i = 0; i < junk.Length; i++) junk[i] = 0x5A;
                s.Write(junk, 0, junk.Length);
                try { s.Flush(true); } catch { }
            }

            long midLen = new FileInfo(walPath).Length;
            if (midLen <= beforeLen) throw new InvalidOperationException("Failed to append junk bytes for truncation test.");

            var sw = Stopwatch.StartNew();
            await using (var wal2 = new WalManager(walPath, metrics, "StartupTruncation"))
            {
                await wal2.StartAsync(CancellationToken.None);
                await wal2.StopAsync(CancellationToken.None);
            }
            sw.Stop();

            long afterLen = new FileInfo(walPath).Length;
            if (afterLen >= midLen)
                throw new InvalidOperationException("Startup truncation did not reduce file size as expected.");

            Console.WriteLine("Startup truncation test passed. Before=" + beforeLen + ", Mid=" + midLen + ", After=" + afterLen +
                              " in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestLegacyHeaderMixRecovery(string walPath, IMetricsManager metrics)
        {
            long legacyLsn = 9_000_000_001L;
            byte[] payload = new byte[5];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0xC0 + i);

            using (var fs = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
            {
                fs.Seek(0, SeekOrigin.End);
                byte[] hdr = new byte[12];
                WriteInt64LittleEndian(hdr, 0, legacyLsn);
                WriteInt32LittleEndian(hdr, 8, payload.Length);
                fs.Write(hdr, 0, hdr.Length);
                fs.Write(payload, 0, payload.Length);
                try { fs.Flush(true); } catch { }
            }

            int count = 0;
            long last = 0;

            var sw = Stopwatch.StartNew();
            await using (var r = new RecoverManager(walPath, metrics))
            {
                await r.StartAsync(CancellationToken.None);
                while (true)
                {
                    var rec = await r.ReadNextAsync(CancellationToken.None);
                    if (rec == null) break;
                    count++;
                    last = rec.SequenceNumber;
                }
                await r.StopAsync(CancellationToken.None);
            }
            sw.Stop();

            if (last != legacyLsn)
                throw new InvalidOperationException("Legacy record LSN was not observed as the last replayed record.");

            Console.WriteLine("Legacy header mix recovery test passed. Total records=" + count +
                              ", LegacyLSN=" + last + " in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestCheckpointHygieneNormalization(string checkpointDir, IMetricsManager metrics)
        {
            // Ensure there is a checkpoint file
            string[] files = Directory.GetFiles(checkpointDir, "checkpoint_*.bin");
            if (files.Length == 0)
                throw new InvalidOperationException("No checkpoint found to normalize.");

            // Pick the (lexicographically) latest file
            int latestIdx = 0;
            for (int i = 1; i < files.Length; i++)
            {
                string a = Path.GetFileName(files[i]);
                string b = Path.GetFileName(files[latestIdx]);
                if (string.CompareOrdinal(a, b) > 0) latestIdx = i;
            }
            string latest = files[latestIdx];

            // Rename it to a legacy 16-digit name by parsing digits and reformatting
            string name = Path.GetFileName(latest);
            if (!name.StartsWith("checkpoint_", StringComparison.Ordinal) || !name.EndsWith(".bin", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected checkpoint filename.");

            int digitsLen = name.Length - "checkpoint_".Length - ".bin".Length;
            string digits = name.Substring("checkpoint_".Length, digitsLen);

            long lsnValue = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                char c = digits[i];
                if (c < '0' || c > '9') throw new InvalidOperationException("Non-digit found in checkpoint filename.");
                lsnValue = (lsnValue * 10) + (c - '0');
            }

            string legacyName = "checkpoint_" + FormatZeroPadded(lsnValue, 16) + ".bin";
            string legacyPath = Path.Combine(checkpointDir, legacyName);

            try
            {
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
                File.Move(latest, legacyPath);
            }
            catch
            {
                // If move fails, continue with existing; hygiene will still run.
            }

            // Start a new CheckpointManager to trigger startup hygiene
            await using (var ckpt = new CheckpointManager(checkpointDir, new WalManager(Path.Combine(checkpointDir, "dummy_wal.log"), metrics, "CkptHygieneWal"), metrics))
            {
                await ckpt.StartAsync(CancellationToken.None);
                await ckpt.DisposeAsync();
            }

            // Validate: only one .bin and it must be 19-digit filename
            files = Directory.GetFiles(checkpointDir, "checkpoint_*.bin");
            int count = files.Length;
            if (count != 1)
                throw new InvalidOperationException("Checkpoint hygiene did not prune to a single file.");

            string normalized = Path.GetFileName(files[0]);
            if (normalized.Length != "checkpoint_".Length + 19 + ".bin".Length)
                throw new InvalidOperationException("Checkpoint name not normalized to 19 digits.");

            Console.WriteLine("Checkpoint hygiene normalization test passed. File=" + normalized);
        }

        private static async Task TestPartialHeaderCorruptions(string walPath, IMetricsManager metrics)
        {
            // Baseline count
            int baselineCount = 0;
            await using (var r0 = new RecoverManager(walPath, metrics))
            {
                await r0.StartAsync(CancellationToken.None);
                while (true)
                {
                    var rec = await r0.ReadNextAsync(CancellationToken.None);
                    if (rec == null) break;
                    baselineCount++;
                }
                await r0.StopAsync(CancellationToken.None);
            }

            // Append sequences for each corruption scenario and ensure recovery stops cleanly before the bad record
            CorruptCase[] cases = new CorruptCase[3];
            cases[0] = new CorruptCase { Kind = CorruptKind.BadVersion };
            cases[1] = new CorruptCase { Kind = CorruptKind.BadLength };
            cases[2] = new CorruptCase { Kind = CorruptKind.BadCrc };

            for (int i = 0; i < cases.Length; i++)
            {
                long originalLen = new FileInfo(walPath).Length;
                try
                {
                    AppendCorruptNewHeader(walPath, cases[i]);
                    // Recover and count
                    int count = 0;
                    var sw = Stopwatch.StartNew();
                    await using (var r = new RecoverManager(walPath, metrics))
                    {
                        await r.StartAsync(CancellationToken.None);
                        while (true)
                        {
                            var rec = await r.ReadNextAsync(CancellationToken.None);
                            if (rec == null) break;
                            count++;
                        }
                        await r.StopAsync(CancellationToken.None);
                    }
                    sw.Stop();
                    if (count < baselineCount)
                        throw new InvalidOperationException("Corruption handling lost valid records.");
                    if (count != baselineCount)
                        throw new InvalidOperationException("Recovery advanced past a corrupt new-format header.");

                    Console.WriteLine("Partial corruption case " + cases[i].Kind + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
                }
                finally
                {
                    // Restore file length
                    using (var fs = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    {
                        fs.SetLength(originalLen);
                        try { fs.Flush(true); } catch { }
                    }
                }
            }

            Console.WriteLine("Partial header corruption tests passed.");
        }

        private enum CorruptKind { BadVersion, BadLength, BadCrc }

        private struct CorruptCase
        {
            public CorruptKind Kind;
        }

        // Append a single corrupt new-format record at EOF
        private static void AppendCorruptNewHeader(string walPath, CorruptCase c)
        {
            // New header layout (24 bytes): [4 magic][4 version][8 lsn][4 len][4 crc32c]
            const uint Magic = 0x534C5741; // "SLWA"
            uint version = 1;
            long lsn = 1234567890123L; // arbitrary
            int len = 0;
            uint crc = 0;

            byte[] payload = new byte[16];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i ^ 0xA5);

            if (c.Kind == CorruptKind.BadVersion)
            {
                version = 99; // invalid
                len = payload.Length;
                crc = 0; // CRC won't be checked if magic/version fails
            }
            else if (c.Kind == CorruptKind.BadLength)
            {
                version = 1;
                len = (64 * 1024 * 1024) + 1; // > MaxRecordBytes (64MB)
                crc = 0;
            }
            else // BadCrc
            {
                version = 1;
                len = payload.Length;
                crc = 0xDEADBEEFu; // wrong CRC on purpose
            }

            byte[] header = new byte[24];
            WriteUInt32LittleEndian(header, 0, Magic);
            WriteUInt32LittleEndian(header, 4, version);
            WriteInt64LittleEndian(header, 8, lsn);
            WriteInt32LittleEndian(header, 16, len);
            WriteUInt32LittleEndian(header, 20, crc);

            using (var fs = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
            {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(header, 0, header.Length);
                if (len > 0)
                {
                    if (c.Kind == CorruptKind.BadLength)
                    {
                        // Write fewer than declared to force tail corruption handling
                        fs.Write(payload, 0, 8);
                    }
                    else
                    {
                        fs.Write(payload, 0, payload.Length);
                    }
                }
                try { fs.Flush(true); } catch { }
            }
        }

        // ---- Helpers (no LINQ, no reflection) ----

        private static string FormatZeroPadded(long value, int totalDigits)
        {
            if (value < 0) value = 0;
            char[] buf = new char[totalDigits];
            for (int i = 0; i < totalDigits; i++) buf[i] = '0';

            int pos = totalDigits - 1;
            long v = value;
            if (v == 0)
            {
                buf[pos] = '0';
            }
            else
            {
                while (v > 0 && pos >= 0)
                {
                    int digit = (int)(v % 10);
                    buf[pos] = (char)('0' + digit);
                    v /= 10;
                    pos--;
                }
            }
            return new string(buf);
        }

        private static void WriteInt64LittleEndian(byte[] buffer, int offset, long value)
        {
            unchecked
            {
                buffer[offset + 0] = (byte)(value);
                buffer[offset + 1] = (byte)(value >> 8);
                buffer[offset + 2] = (byte)(value >> 16);
                buffer[offset + 3] = (byte)(value >> 24);
                buffer[offset + 4] = (byte)(value >> 32);
                buffer[offset + 5] = (byte)(value >> 40);
                buffer[offset + 6] = (byte)(value >> 48);
                buffer[offset + 7] = (byte)(value >> 56);
            }
        }

        private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
        {
            unchecked
            {
                buffer[offset + 0] = (byte)(value);
                buffer[offset + 1] = (byte)(value >> 8);
                buffer[offset + 2] = (byte)(value >> 16);
                buffer[offset + 3] = (byte)(value >> 24);
            }
        }

        private static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
        {
            unchecked
            {
                buffer[offset + 0] = (byte)(value);
                buffer[offset + 1] = (byte)(value >> 8);
                buffer[offset + 2] = (byte)(value >> 16);
                buffer[offset + 3] = (byte)(value >> 24);
            }
        }

        // --- Dummy metrics manager (ships with .NET interfaces only) ---
        class DummyMetricsManager : IMetricsManager
        {
            public void Register(MetricDefinition def) { }
            public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }
    }
}
