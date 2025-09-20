using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;
using Silica.Durability.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Exceptions;

namespace Silica.Durability
{
    /// <summary>
    /// Single-consumer, async-only WAL replay manager with full metrics instrumentation.
    /// </summary>
    public sealed class RecoverManager : IRecoverManager
    {
        private readonly string _path;
        private FileStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _started;
        private bool _disposed;

        private readonly IMetricsManager _metrics;
        private readonly string _componentName = nameof(RecoverManager);

        static RecoverManager()
        {
            try { DurabilityExceptions.RegisterAll(); } catch { }
        }

        public RecoverManager(string walFilePath, IMetricsManager metrics)
        {
            _path = walFilePath ?? throw new ArgumentNullException(nameof(walFilePath));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

            // Pre-register all recovery metrics under component=RecoverManager
            RecoveryMetrics.RegisterAll(_metrics, _componentName);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_started) throw new RecoverManagerAlreadyStartedException();

            var sw = Stopwatch.StartNew();
            try
            {
                // Open (or create) WAL file for replay
                _stream = new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                _stream.Seek(0, SeekOrigin.Begin);
                _started = true;
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(RecoveryMetrics.StartCount.Name);
                _metrics.Record(RecoveryMetrics.StartDurationMs.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async Task<WalRecord?> ReadNextAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new RecoverManagerNotStartedException();

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
                // EOF check
                if (_stream!.Position >= _stream.Length)
                {
                    sw.Stop();
                    return null;
                }

                // Try to read new header first (24 bytes). If magic/version mismatch, fall back to legacy 12-byte header.
                byte[] hdr = ArrayPool<byte>.Shared.Rent(WalFormat.NewHeaderSize);
                int hdrLen = hdr.Length;
                try
                {
                    // Peek first 4 bytes
                    int read = await ReadExactlyAsync(_stream!, hdr, 0, 4, cancellationToken).ConfigureAwait(false);
                    if (read < 4)
                    {
                        sw.Stop();
                        return null;
                    }
                    long lsn;
                    int payloadLen;
                    bool isNew = false;
                    uint expectedCrc = 0;
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4));
                    if (magic == WalFormat.Magic)
                    {
                        int remain = WalFormat.NewHeaderSize - 4;
                        int nr = await ReadExactlyAsync(_stream!, hdr, 4, remain, cancellationToken).ConfigureAwait(false);
                        if (nr < remain)
                        {
                            // Truncated header at tail
                            _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                            sw.Stop();
                            return null;
                        }
                        uint crc;
                        if (!WalFormat.TryParseNewHeader(hdr.AsSpan(0, WalFormat.NewHeaderSize), out lsn, out payloadLen, out crc))
                        {
                            _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                            sw.Stop();
                            return null;
                        }
                        expectedCrc = crc;
                        isNew = true;
                    }
                    else
                    {
                        // Legacy header: read remaining 8 bytes to complete 12-byte header
                        byte[] legacy = ArrayPool<byte>.Shared.Rent(WalFormat.LegacyHeaderSize);
                        int legacyLen = legacy.Length;
                        bool legacyRented = true;
                        try
                        {
                            legacy[0] = hdr[0]; legacy[1] = hdr[1]; legacy[2] = hdr[2]; legacy[3] = hdr[3];
                            int lr = await ReadExactlyAsync(_stream!, legacy, 4, WalFormat.LegacyHeaderSize - 4, cancellationToken).ConfigureAwait(false);
                            if (lr < WalFormat.LegacyHeaderSize - 4)
                            {
                                // Truncated legacy header at tail: treat as clean EOF with tail corruption metric.
                                _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                                sw.Stop();
                                return null;
                            }
                            lsn = BinaryPrimitives.ReadInt64LittleEndian(legacy.AsSpan(0, 8));
                            payloadLen = BinaryPrimitives.ReadInt32LittleEndian(legacy.AsSpan(8, 4));
                        }
                        finally
                        {
                            if (legacyRented)
                            {
                                System.Array.Clear(legacy, 0, legacyLen);
                                ArrayPool<byte>.Shared.Return(legacy);
                            }
                        }
                    }

                    if (payloadLen < 0 || payloadLen > WalFormat.MaxRecordBytes)
                    {
                        _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                        sw.Stop();
                        return null;
                    }
                    // Read payload exactly (allocate dedicated buffer to avoid ArrayPool lifetime leaks)
                    ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
                    if (payloadLen > 0)
                    {
                        byte[] payloadBuf = new byte[payloadLen];
                        int pr = await ReadExactlyAsync(_stream!, payloadBuf, 0, payloadLen, cancellationToken).ConfigureAwait(false);
                        if (pr != payloadLen)
                        {
                            _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                            sw.Stop();
                            return null;
                        }
                        payload = new ReadOnlyMemory<byte>(payloadBuf, 0, payloadLen);
                    }
                    // Validate CRC for new-format records before returning
                    if (isNew)
                    {
                        uint actual = payloadLen == 0 ? 0u : WalFormat.ComputeCrc32C(payload.Span);
                        if (actual != expectedCrc)
                        {
                            _metrics.Increment(RecoveryMetrics.CorruptTailCount.Name);
                            sw.Stop();
                            return null;
                        }
                    }
                    sw.Stop();
                    if (payloadLen > 0)
                    {
                        _metrics.Record(RecoveryMetrics.PayloadBytesRead.Name, payloadLen);
                    }
                    _metrics.Increment(RecoveryMetrics.RecordReadCount.Name);
                    _metrics.Record(RecoveryMetrics.RecordReadDurationMs.Name, sw.Elapsed.TotalMilliseconds);

                    return new WalRecord(lsn, payload);
                }
                finally
                {
                    System.Array.Clear(hdr, 0, hdrLen);
                    ArrayPool<byte>.Shared.Return(hdr);
                }
            }
            catch
            {
                sw.Stop();
                _metrics.Increment(RecoveryMetrics.RecordReadFailures.Name);
                throw;
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
                // Prevent new reads
                _started = false;
                // Coordinate with any in-flight ReadNextAsync to avoid disposing the stream under it.
                // Use CancellationToken.None to guarantee cleanup even if caller cancels.
                await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var s = _stream;
                    _stream = null;
                    if (s != null)
                    {
                        s.Dispose();
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(RecoveryMetrics.StopCount.Name);
                _metrics.Record(RecoveryMetrics.StopDurationMs.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow dispose-time errors */ }
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
                throw new RecoverManagerDisposedException();
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
    }
}
