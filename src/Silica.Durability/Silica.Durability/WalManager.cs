using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Buffers;
using Silica.Common;
using Silica.Durability;
using Silica.Durability.Metrics;
using Silica.DiagnosticsCore.Metrics;
using System.Runtime.InteropServices;
using System.Threading;


namespace Silica.Durability
{
    /// <summary>
    /// Thread-safe, async WAL append manager with full metrics instrumentation.
    /// </summary>
    public sealed class WalManager : IWalManager
    {
        private readonly string _path;
        private FileStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _lsnCounter;
        private long _writtenLsn; // highest LSN successfully written to the file
        private long _flushedLsn; // highest LSN durably flushed to stable storage
        private bool _started;
        private bool _disposed;

        private readonly IMetricsManager _metrics;
        private readonly string _walName;

        /// <summary>
        /// Initializes a new WAL manager and registers all WAL metrics.
        /// </summary>
        public WalManager(
            string logFilePath,
            IMetricsManager metrics,
            string? walName = null)
        {
            _path = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _walName = string.IsNullOrWhiteSpace(walName) ? nameof(WalManager) : walName;

            // Pre-register all WAL metrics (start/stop, append, flush, lock wait, in-flight, latest LSN)
            WalMetrics.RegisterAll(
                _metrics,
                _walName,
                // Report the latest successfully written LSN to avoid exposing unpersisted allocations
                latestLsnProvider: () => Interlocked.Read(ref _writtenLsn));
        }

        public async Task StartAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("WAL already started.");

            var sw = Stopwatch.StartNew();
            try
            {
                // Ensure directory exists for the WAL file path
                try
                {
                    string? dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch
                {
                    // Allow downstream open to throw if directory establishment truly fails
                }
                // 1) Scan existing file to find highest LSN
                if (File.Exists(_path))
                {
                    using var reader = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 4096,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                    long last = 0;
                    // Try new header first; if magic doesn't match, fall back to legacy.
                    var newHeader = new byte[WalFormat.NewHeaderSize];
                    var legacyHeader = new byte[WalFormat.LegacyHeaderSize];
                    byte[] crcBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024); // 1MB reusable buffer
                    while (reader.Position < reader.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Peek first 4 bytes to check magic without advancing incorrectly on legacy
                        long startPos = reader.Position;
                        int peekRead = await ReadExactlyAsync(reader, newHeader, 0, 4, ct).ConfigureAwait(false);
                        if (peekRead < 4)
                        {
                            // Partial at tail: stop scanning; last is valid
                            break;
                        }

                        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(newHeader.AsSpan(0, 4));
                        if (magic == WalFormat.Magic)
                        {
                            // Read the rest of the new header
                            int remain = WalFormat.NewHeaderSize - 4;
                            int nr = await ReadExactlyAsync(reader, newHeader, 4, remain, ct).ConfigureAwait(false);
                            if (nr < remain) { TruncateTail(_path, reader, startPos); break; }
                            long lsn;
                            int len;
                            uint crc;
                            if (!WalFormat.TryParseNewHeader(newHeader, out lsn, out len, out crc))
                            {
                                // Invalid header at tail: truncate back to start of this record.
                                TruncateTail(_path, reader, startPos);
                                break;
                            }
                            if (len < 0 || len > WalFormat.MaxRecordBytes)
                            {
                                // Invalid length encountered: treat as tail corruption and truncate.
                                TruncateTail(_path, reader, startPos);
                                break;
                            }
                            long endOfPayload = reader.Position + len;
                            if (endOfPayload > reader.Length) { TruncateTail(_path, reader, startPos); break; }
                            // Validate CRC to ensure tail integrity; reading advances to end of payload
                            if (len > 0)
                            {
                                if (len > crcBuffer.Length)
                                {
                                    ArrayPool<byte>.Shared.Return(crcBuffer);
                                    crcBuffer = ArrayPool<byte>.Shared.Rent(len);
                                }
                                int pr = await ReadExactlyAsync(reader, crcBuffer, 0, len, ct).ConfigureAwait(false);
                                if (pr != len)
                                {
                                    TruncateTail(_path, reader, startPos);
                                    break;
                                }
                                uint actual = WalFormat.ComputeCrc32C(new ReadOnlySpan<byte>(crcBuffer, 0, len));
                                if (actual != crc)
                                {
                                    TruncateTail(_path, reader, startPos);
                                    break;
                                }
                            }
                            // Success: we're already positioned after the payload. Update last and continue.
                            if (lsn > last) last = lsn;
                        }
                        else
                        {
                            // Legacy header path: reset to startPos and read 12 bytes
                            reader.Seek(startPos, SeekOrigin.Begin);
                            int n = await ReadExactlyAsync(reader, legacyHeader, 0, legacyHeader.Length, ct).ConfigureAwait(false);
                            if (n < legacyHeader.Length)
                            {
                                TruncateTail(_path, reader, startPos);
                                break;
                            }
                            long lsn = BinaryPrimitives.ReadInt64LittleEndian(legacyHeader);
                            int len = BinaryPrimitives.ReadInt32LittleEndian(legacyHeader.AsSpan(8));
                            if (len < 0 || len > WalFormat.MaxRecordBytes)
                            {
                                // Invalid legacy record length: truncate at this boundary.
                                TruncateTail(_path, reader, startPos);
                                break;
                            }
                            long newPos = reader.Position + len;
                            if (newPos > reader.Length)
                            {
                                TruncateTail(_path, reader, startPos);
                                break;
                            }
                            reader.Seek(len, SeekOrigin.Current);
                            if (lsn > last) last = lsn;
                        }
                    }
                    // Clear a conservative slice (up to 1 MB) rather than the entire rented buffer
                    // to reduce restart-time costs when large buffers were rented.
                    int toClear = crcBuffer.Length;
                    if (toClear > (1024 * 1024)) toClear = 1024 * 1024;
                    Array.Clear(crcBuffer, 0, toClear);
                    ArrayPool<byte>.Shared.Return(crcBuffer);
                    Interlocked.Exchange(ref _lsnCounter, last);
                    Interlocked.Exchange(ref _writtenLsn, last);
                    Interlocked.Exchange(ref _flushedLsn, last);
                }

                // 2) Open writer at end-of-file
                _stream = new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    // Allow readers but prevent a second writer in the same process/machine context.
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);
                _stream.Seek(0, SeekOrigin.End);

                // Best-effort advisory lock to guard against accidental multi-writer scenarios.
                try
                {
                    // Lock a very large range to emulate a whole-file advisory lock.
                    // Length must be positive (0 is invalid and may throw).
                    _stream.Lock(0, long.MaxValue);
                }
                catch
                {
                    // Some platforms or filesystems may not support this; ignore to remain cross-platform.
                }
                _started = true;
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(WalMetrics.StartCount.Name);
                _metrics.Record(WalMetrics.StartDurationMs.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async Task AppendAsync(WalRecord record, CancellationToken cancellationToken)
        {
            // Single append; compare producer-provided sequence number to the assigned LSN for observability only.
            long assignedLsn = await AppendCoreAsync(record, cancellationToken).ConfigureAwait(false);
            if (record.SequenceNumber != assignedLsn)
            {
                _metrics.Increment(WalMetrics.SequenceMismatchCount.Name);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");

            // 1) Wait for lock
            var waitSw = Stopwatch.StartNew();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSw.Stop();
            _metrics.Record(WalMetrics.LockWaitDurationMs.Name, waitSw.Elapsed.TotalMilliseconds);

            try
            {
                // 2) Flush to disk
                var ioSw = Stopwatch.StartNew();
                // Capture before/after lengths while holding the lock for consistency
                long preFlushLength = _stream!.Length;
                // Ensure data is committed to stable storage
                _stream!.Flush(true);
                long postFlushLength = _stream!.Length;
                if (postFlushLength < preFlushLength)
                {
                    _metrics.Increment(WalMetrics.FlushRegressionCount.Name);
                    Trace.WriteLine($"[WalManager] WAL file shrank from {preFlushLength} to {postFlushLength} after flush.");
                }

                ioSw.Stop();
                // Only mark as flushed what we know was written
                Interlocked.Exchange(ref _flushedLsn, Interlocked.Read(ref _writtenLsn));
                _metrics.Increment(WalMetrics.FlushCount.Name);
                _metrics.Record(WalMetrics.FlushDurationMs.Name, ioSw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) return;

            var sw = Stopwatch.StartNew();
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                // Make shutdown deterministic: prevent new appends, then serialize with in-flight writers.
                _started = false;
                await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    // Close the stream to release file handles (symmetry with RecoverManager)
                    var s = _stream;
                    _stream = null;
                    if (s != null)
                    {
                        try { s.Unlock(0, long.MaxValue); } catch { }
                        s.Dispose();
                    }
                }
                finally
                {
                    _lock.Release();
                }
                _metrics.Increment(WalMetrics.StopCount.Name);
            }
            finally
            {
                sw.Stop();
                _metrics.Record(WalMetrics.StopDurationMs.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort: log and swallow during dispose
                try { Trace.WriteLine($"[WalManager] DisposeAsync encountered an error: {ex}"); } catch { }
            }
            finally
            {
                try { _stream?.Dispose(); } catch { }
                _lock.Dispose();
                _disposed = true;
            }

        }

        public void Dispose() =>
            DisposeAsync().AsTask().GetAwaiter().GetResult();

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalManager));
        }

        public Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");
            // Return the highest successfully written LSN (not merely allocated).
            return Task.FromResult(Interlocked.Read(ref _writtenLsn));
        }

        public Task<long> GetFlushedSequenceNumberAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");
            // Read is atomic for 64-bit via Interlocked
            return Task.FromResult(Interlocked.Read(ref _flushedLsn));
        }

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

        // Optional ergonomic addition: return the assigned LSN to callers that need it.
        // Non-breaking: existing AppendAsync remains available.
        public async Task<long> AppendReturningLsnAsync(WalRecord record, CancellationToken cancellationToken)
        {
            // Use the core path to get the exact LSN persisted by this append.
            return await AppendCoreAsync(record, cancellationToken).ConfigureAwait(false);
        }
        // Core append that performs the write and returns the exact LSN persisted by this call.
        private async Task<long> AppendCoreAsync(WalRecord record, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");

            // Validate payload size early
            int payloadLenChecked = record.Payload.Length;
            if (payloadLenChecked < 0 || payloadLenChecked > WalFormat.MaxRecordBytes)
                throw new ArgumentOutOfRangeException(nameof(record), "Payload length exceeds maximum allowed size.");

            // Ensure write-order == LSN-order
            var waitSw = Stopwatch.StartNew();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSw.Stop();
            _metrics.Record(WalMetrics.LockWaitDurationMs.Name, waitSw.Elapsed.TotalMilliseconds);

            long lsn = 0;
            byte[] header = ArrayPool<byte>.Shared.Rent(WalFormat.NewHeaderSize);
            try
            {
                // Allocate LSN inside the lock
                lsn = Interlocked.Increment(ref _lsnCounter);

                // Build header
                int payloadLen = payloadLenChecked;
                uint crc = payloadLen == 0 ? 0u : WalFormat.ComputeCrc32C(record.Payload.Span);
                WalFormat.WriteHeader(header, lsn, payloadLen, crc);

                // Write header + payload to disk
                var ioSw = Stopwatch.StartNew();
                await _stream!.WriteAsync(header, 0, WalFormat.NewHeaderSize, cancellationToken).ConfigureAwait(false);
                if (!record.Payload.IsEmpty)
                {
                    ArraySegment<byte> seg;
                    if (MemoryMarshal.TryGetArray(record.Payload, out seg))
                    {
                        await _stream.WriteAsync(seg.Array!, seg.Offset, seg.Count, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var tmp = record.Payload.ToArray();
                        await _stream.WriteAsync(tmp, 0, tmp.Length, cancellationToken).ConfigureAwait(false);
                        // Best-effort: clear copied payload to limit exposure in managed memory
                        try { System.Array.Clear(tmp, 0, tmp.Length); } catch { }
                    }
                }
                ioSw.Stop();

                // Publish as successfully written
                Interlocked.Exchange(ref _writtenLsn, lsn);
                // Note: durability requires an explicit FlushAsync(). Until then, the written LSN
                // is not considered durably flushed and GetFlushedSequenceNumberAsync will lag.
                _metrics.Increment(WalMetrics.AppendCount.Name);
                _metrics.Record(WalMetrics.AppendDurationMs.Name, ioSw.Elapsed.TotalMilliseconds);
                _metrics.Record(WalMetrics.BytesAppended.Name, WalFormat.NewHeaderSize + payloadLen);

                return lsn;
            }
            finally
            {
                // Clear header to avoid leaking LSN/payload length/CRC through the pool
                System.Array.Clear(header, 0, WalFormat.NewHeaderSize);
                ArrayPool<byte>.Shared.Return(header);
                _lock.Release();
            }
        }

        // Centralize tail truncation for auditability and correctness.
        private void TruncateTail(string path, FileStream reader, long safeLength)
        {
            reader.Dispose();
            using (var trunc = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                trunc.SetLength(safeLength);
                // Best-effort flush of metadata to commit truncation
                try { trunc.Flush(true); } catch { }
            }
            try
            {
                Trace.WriteLine($"[WalManager] Truncated tail at offset {safeLength} for file '{path}'");
            }
            catch { /* ignore if Trace unavailable */ }

            try
            {
                _metrics.Increment(WalMetrics.TailTruncateCount.Name);
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("[WalManager] Metric increment failed in TruncateTail: " + ex.Message); } catch { }
            }

            // Removed duplicate metric increment to avoid double-counting.

            // Best-effort: flush directory metadata so the new file size is promptly visible
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    using (var dirHandle = File.Open(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        dirHandle.Flush(true);
                    }
                }
            }
            catch
            {
                // Cross-platform tolerant; directory flush may not be supported
                _metrics.Increment(WalMetrics.DirectoryFlushUnsupportedCount.Name);
                try { Trace.WriteLine($"[WalManager] Directory flush not supported after truncation for '{path}'"); } catch { }
            }
        }
    }
}
