using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Diagnostics;
using Silica.Durability.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Exceptions;

namespace Silica.Durability
{
    /// <summary>
    /// Coordinates writing and reading durable checkpoints for WAL-based recovery.
    /// </summary>
    public sealed class CheckpointManager : ICheckpointManager
    {
        private const string CheckpointFilePattern = "checkpoint_*.bin";
        private const int CheckpointRecordBytes = 16;
        private const string CheckpointPrefix = "checkpoint_";
        private const string CheckpointSuffix = ".bin";
        // Use full Int64 width for lexicographic filename ordering and future-proofing.
        // Accept older 16-digit names at read/hygiene; write new files at 19 digits.
        private const int CheckpointDigitsLegacy = 16;
        private const int CheckpointDigitsFull = 19;

        private readonly string _directory;
        private readonly IWalManager _wal;
        // Gate to ensure only one checkpoint write/prune at a time
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly IMetricsManager _metrics;
        private readonly string _componentName = nameof(CheckpointManager);

        private bool _started;
        private bool _disposed;

        static CheckpointManager()
        {
            try { DurabilityExceptions.RegisterAll(); } catch { }
        }

        public CheckpointManager(
            string checkpointDirectory,
            IWalManager walManager,
            IMetricsManager metrics)
        {
            _directory = checkpointDirectory
                ?? throw new ArgumentNullException(nameof(checkpointDirectory));
            _wal = walManager
                ?? throw new ArgumentNullException(nameof(walManager));
            _metrics = metrics
                ?? throw new ArgumentNullException(nameof(metrics));

            // Pre-register all checkpoint metrics under component=CheckpointManager
            CheckpointMetrics.RegisterAll(_metrics, _componentName);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_started)
                throw new CheckpointManagerAlreadyStartedException();

            Directory.CreateDirectory(_directory);

            // Clean up any orphaned temp files from previous crashes
            var files = Directory.GetFiles(_directory, "checkpoint_*.tmp");
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(files[i]); } catch { /* ignore */ }
            }

            // Optional hygiene: prune malformed .bin files and keep only the latest valid checkpoint
            try
            {
                string[] bins = Directory.GetFiles(_directory, CheckpointFilePattern);
                if (bins.Length > 0)
                {
                    // Normalize names to content LSN, delete malformed, then prune to the single latest
                    long latestLsn = -1;
                    string? latestPath = null;
                    for (int i = 0; i < bins.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path = bins[i];
                        try
                        {
                            var fi = new FileInfo(path);
                            if (!fi.Exists || fi.Length != CheckpointRecordBytes)
                            {
                                try { File.Delete(path); } catch { }
                                continue;
                            }
                            long contentLsn;
                            long ticks;
                            if (!TryReadCheckpointContent(path, out contentLsn, out ticks))
                            {
                                try { File.Delete(path); } catch { }
                                continue;
                            }
                            // Always normalize to the full-width (19-digit) filename
                            string expectedName = BuildCheckpointFileName(contentLsn);
                            string expectedPath = Path.Combine(_directory, expectedName);
                            if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.Ordinal))
                            {
                                try
                                {
                                    // Normalize filename to match content LSN; prefer overwrite to dedupe
                                    File.Move(path, expectedPath, overwrite: true);
                                    path = expectedPath;
                                }
                                catch
                                {
                                    // If rename fails, best-effort: keep original if it points to the same LSN
                                }
                            }
                            if (contentLsn > latestLsn)
                            {
                                latestLsn = contentLsn;
                                latestPath = path;
                            }
                        }
                        catch
                        {
                            try { File.Delete(path); } catch { }
                        }
                    }
                    // Prune everything except latestPath (if any)
                    if (latestPath != null)
                    {
                        bins = Directory.GetFiles(_directory, CheckpointFilePattern);
                        for (int i = 0; i < bins.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string p = bins[i];
                            if (!string.Equals(Path.GetFileName(p), Path.GetFileName(latestPath), StringComparison.Ordinal))
                            {
                                try { File.Delete(p); } catch { }
                            }
                        }
                        // Best-effort flush of directory metadata
                        try
                        {
                            using (var dirHandle = File.Open(_directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                dirHandle.Flush(true);
                            }
                        }
                        catch
                        {
                            // Parity with write/prune: surface unsupported directory flush capability
                            try { _metrics.Increment(CheckpointMetrics.DirectoryFlushUnsupportedCount.Name); } catch { }
                            try { Trace.WriteLine($"[CheckpointManager] Directory flush not supported for '{_directory}' (startup hygiene)"); } catch { }
                        }
                    }
                }
            }
            catch { /* hygiene best-effort */ }

            _started = true;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task WriteCheckpointAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfNotStarted();
            ThrowIfDisposed();

            // Fast-path: skip if nothing new to checkpoint
            long lastLsn = await _wal.GetFlushedSequenceNumberAsync(cancellationToken).ConfigureAwait(false);
            long existing = TryGetLatestCheckpointLsnNoThrow();
            if (existing == lastLsn && existing >= 0)
            {
                _metrics.Increment(CheckpointMetrics.SkippedWriteCount.Name);
                return;
            }

            var gateWait = Stopwatch.StartNew();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateWait.Stop();
            _metrics.Record(CheckpointMetrics.LockWaitDurationMs.Name, gateWait.Elapsed.TotalMilliseconds);
            var writeSw = Stopwatch.StartNew();
            var pruneSw = new Stopwatch();
            int pruned = 0;
            string? tmpPath = null;
            string? outPath = null;

            try
            {
                // Re-check disposal/writable state only inside try/finally so the gate is always released.
                if (_disposed || !_started)
                {
                    throw new CheckpointNotWritableStateException();
                }
                //
                // WRITE PHASE
                //
                await _wal.FlushAsync(cancellationToken).ConfigureAwait(false);

                lastLsn = await _wal.GetFlushedSequenceNumberAsync(cancellationToken).ConfigureAwait(false);
                existing = TryGetLatestCheckpointLsnNoThrow();
                if (existing == lastLsn && existing >= 0)
                {
                    writeSw.Stop();
                    _metrics.Increment(CheckpointMetrics.SkippedWriteCount.Name);
                    return;
                }

                string fileName = BuildCheckpointFileName(lastLsn);
                tmpPath = Path.Combine(_directory, fileName + ".tmp");
                outPath = Path.Combine(_directory, fileName);

                var buffer = new byte[CheckpointRecordBytes];
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, 8), lastLsn);
                long ticks = DateTime.UtcNow.Ticks;
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8, 8), ticks);

                using (var fs = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await fs.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    fs.Flush(true);
                }

                File.Move(tmpPath, outPath, overwrite: true);

                if (!File.Exists(outPath))
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    if (!File.Exists(outPath))
                        throw new CheckpointRenameNotVisibleException(outPath);
                }

                var fi = new FileInfo(outPath);
                _metrics.Record(CheckpointMetrics.CheckpointFileBytes.Name, fi.Length);
                if (fi.Length != CheckpointRecordBytes)
                    throw new CheckpointFileCorruptException(outPath, CheckpointRecordBytes, fi.Length);

                // Reopen and verify content to ensure rename visibility and correctness under FS edge cases
                long verifyLsn, verifyTicks;
                if (!TryReadCheckpointContent(outPath, out verifyLsn, out verifyTicks) || verifyLsn != lastLsn)
                {
                    // Treat as corruption if the content doesn't match our intended LSN
                    throw new CheckpointFileCorruptException(outPath, CheckpointRecordBytes, fi.Length);
                }

                try
                {
                    using (var dirHandle = File.Open(_directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        dirHandle.Flush(true);
                    }
                }
                catch
                {
                    try { _metrics.Increment(CheckpointMetrics.DirectoryFlushUnsupportedCount.Name); } catch { }
                    try { Trace.WriteLine($"[CheckpointManager] Directory flush not supported for '{_directory}' (post-rename)"); } catch { }
                }

                writeSw.Stop();
                _metrics.Increment(CheckpointMetrics.WriteCount.Name);
                _metrics.Record(CheckpointMetrics.WriteDurationMs.Name, writeSw.Elapsed.TotalMilliseconds);

                //
                // PRUNE PHASE
                //
                pruneSw.Start();
                string[] all = Directory.GetFiles(_directory, CheckpointFilePattern);
                if (all.Length > 1)
                {
                    Array.Sort(all, StringComparer.Ordinal);
                    for (int i = 0; i < all.Length - 1; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            File.Delete(all[i]);
                            pruned++;
                        }
                        catch
                        {
                            try { Trace.WriteLine($"[CheckpointManager] Failed to delete old checkpoint: {all[i]}"); }
                            catch { }
                        }
                    }
                }
                pruneSw.Stop();

                _metrics.Record(CheckpointMetrics.PruneDurationMs.Name, pruneSw.Elapsed.TotalMilliseconds);
                if (pruned > 0)
                    _metrics.Increment(CheckpointMetrics.PruneCount.Name, pruned);

                try
                {
                    using (var dirHandle = File.Open(_directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        dirHandle.Flush(true);
                    }
                }
                catch
                {
                    try { _metrics.Increment(CheckpointMetrics.DirectoryFlushUnsupportedCount.Name); } catch { }
                    try { Trace.WriteLine($"[CheckpointManager] Directory flush not supported for '{_directory}'"); } catch { }
                }
            }
            catch
            {
                writeSw.Stop();
                _metrics.Increment(CheckpointMetrics.WriteFailures.Name);
                try
                {
                    if (!string.IsNullOrEmpty(tmpPath) && File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch { }
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<CheckpointData?> ReadLatestCheckpointAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfNotStarted();
            ThrowIfDisposed();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    int attempts = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var files = Directory.GetFiles(_directory, CheckpointFilePattern);
                        if (files.Length == 0)
                        {
                            sw.Stop();
                            return null;
                        }
                        // Pick lexicographically largest filename
                        int latestIdx = 0;
                        for (int i = 1; i < files.Length; i++)
                        {
                            if (string.CompareOrdinal(Path.GetFileName(files[i]), Path.GetFileName(files[latestIdx])) > 0)
                                latestIdx = i;
                        }
                        string latest = files[latestIdx];
                        try
                        {
                            // Quick length sanity to avoid partial/truncated reads before opening
                            var info = new FileInfo(latest);
                            if (info.Exists && info.Length != CheckpointRecordBytes)
                            {
                                // Delete malformed and retry previous
                                try { File.Delete(latest); } catch { /* ignore */ }
                                attempts++;
                                if (attempts >= 3)
                                    throw new CheckpointReadFailedException();
                                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            using var fs = new FileStream(
                                latest,
                                FileMode.Open,
                                FileAccess.Read,
                                // Allow concurrent writer to rename/prune via our serialized gate,
                                // but tolerate external scanners or backup agents.
                                FileShare.Read | FileShare.Delete,
                                bufferSize: 4096,
                                options: FileOptions.Asynchronous);
                            var buffer = new byte[CheckpointRecordBytes];
                            int n = await ReadExactlyAsync(fs, buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                            if (n < buffer.Length)
                                throw new CheckpointReadFailedException();
                            long lastLsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
                            long ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8, 8));
                            // Validate ticks to defend against corrupt-but-correct-length files.
                            // DateTime ticks must be within [DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks].
                            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                            {
                                // Treat as corruption: delete and retry older one(s).
                                try { File.Delete(latest); } catch { /* ignore */ }
                                attempts++;
                                if (attempts >= 3)
                                    throw new CheckpointReadFailedException();
                                try { Trace.WriteLine($"[CheckpointManager] Invalid checkpoint ticks in '{latest}' deleted during read attempt {attempts}"); } catch { }
                                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                            var checkpoint = new CheckpointData
                            {
                                LastSequenceNumber = lastLsn,
                                // Reconstruct as UTC to mirror write-side UTC ticks.
                                CreatedUtc = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
                            };

                            sw.Stop();
                            _metrics.Increment(CheckpointMetrics.ReadCount.Name);
                            _metrics.Record(
                                CheckpointMetrics.ReadDurationMs.Name,
                                sw.Elapsed.TotalMilliseconds);

                            return checkpoint;
                        }
                        catch (FileNotFoundException)
                        {
                            // Race with pruning; retry a couple times
                            attempts++;
                            if (attempts >= 2)
                                throw new CheckpointReadFailedException();
                            try { Trace.WriteLine($"[CheckpointManager] Checkpoint file not found during read attempt {attempts}"); } catch { }
                            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        catch (IOException)
                        {
                            // Corrupt or truncated "latest" checkpoint; delete and retry older one(s).
                            // This is safe because WAL replay will still recover from earlier LSN.
                            try { File.Delete(latest); } catch { /* ignore */ }
                            attempts++;
                            if (attempts >= 3)
                                throw new CheckpointReadFailedException();
                            try { Trace.WriteLine($"[CheckpointManager] Corrupt checkpoint file '{latest}' deleted during read attempt {attempts}"); } catch { }
                            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                catch
                {
                    sw.Stop();
                    _metrics.Increment(CheckpointMetrics.ReadFailures.Name);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            try
            {
                // Serialize with in-flight checkpoint operations to avoid disposing the gate under them.
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: proceed to dispose even if we can't acquire due to exceptional state.
            }
            finally
            {
                try { _gate.Release(); } catch { /* may throw if not held; ignore */ }
                _gate.Dispose();
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void ThrowIfNotStarted()
        {
            if (!_started)
                throw new CheckpointManagerNotStartedException();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new CheckpointManagerDisposedException();
        }

        // Optional: make StopAsync explicit in the concrete for discoverability; delegates to DisposeAsync
        public Task StopAsync(CancellationToken cancellationToken = default)
            => DisposeAsync().AsTask();

        private static async Task<int> ReadExactlyAsync(FileStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        // Parses the lexicographically latest checkpoint filename to a long LSN.
        // Returns -1 if none found or parsing fails.
        private long TryGetLatestCheckpointLsnNoThrow()
        {
            try
            {
                string[] files = Directory.GetFiles(_directory, CheckpointFilePattern);
                if (files.Length == 0) return -1;
                // Choose lexicographically largest
                int latestIdx = 0;
                for (int i = 1; i < files.Length; i++)
                {
                    string a = Path.GetFileName(files[i]);
                    string b = Path.GetFileName(files[latestIdx]);
                    if (string.CompareOrdinal(a, b) > 0) latestIdx = i;
                }
                string name = Path.GetFileName(files[latestIdx]);
                // Accept "checkpoint_<digits>.bin" where <digits> length is either legacy 16 or full 19.
                if (name.StartsWith(CheckpointPrefix, StringComparison.Ordinal) &&
                    name.EndsWith(CheckpointSuffix, StringComparison.Ordinal))
                {
                    int digitsLen = name.Length - CheckpointPrefix.Length - CheckpointSuffix.Length;
                    if (digitsLen == CheckpointDigitsLegacy || digitsLen == CheckpointDigitsFull)
                    {
                        int start = CheckpointPrefix.Length;
                        string lsnText = name.Substring(start, digitsLen);
                        bool allDigits = true;
                        for (int i = 0; i < digitsLen; i++)
                        {
                            char c = lsnText[i];
                            if (c < '0' || c > '9') { allDigits = false; break; }
                        }
                        if (allDigits)
                        {
                            long parsed;
                            if (long.TryParse(lsnText, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                                return parsed;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        private static bool TryReadCheckpointContent(string path, out long lsn, out long ticks)
        {
            lsn = -1;
            ticks = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    var buffer = new byte[CheckpointRecordBytes];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int n = fs.Read(buffer, total, buffer.Length - total);
                        if (n == 0) break;
                        total += n;
                    }
                    if (total != CheckpointRecordBytes) return false;
                    lsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
                    ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8, 8));
                    if (lsn < 0) return false;
                    // Defend against corrupt-but-correct-length files: enforce valid DateTime tick range.
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string BuildCheckpointFileName(long lsn)
        {
            // 19-digit zero-padded decimal (full Int64 width), culture-invariant
            // Matches the declared contract and startup hygiene normalization.
            return CheckpointPrefix + lsn.ToString("0000000000000000000", CultureInfo.InvariantCulture) + CheckpointSuffix;
        }
    }
}
