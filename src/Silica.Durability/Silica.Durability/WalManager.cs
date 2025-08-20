// File: Durability/WalManager.cs
using System;
using System.IO;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Silica.Observability.Metrics;
using Silica.Observability.Metrics.Interfaces;

namespace Silica.Durability
{
    public sealed class WalManager : IWalManager
    {
        private readonly string _path;
        private FileStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _lsnCounter;
        private bool _started;
        private bool _disposed;

        // Metrics
        private readonly IMetricsManager _metrics;
        private readonly string _walName;

        // Metrics-enabled ctor
        public WalManager(string logFilePath, IMetricsManager metrics, string? walName = null)
        {
            _path = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _walName = walName ?? GetType().Name;

            // Register all metrics (including observable latest LSN)
            WalMetrics.RegisterAll(
                _metrics,
                _walName,
                lastLsnProvider: () => Interlocked.Read(ref _lsnCounter));
        }

        public async Task StartAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("Already started");

            var sw = Stopwatch.StartNew();
            try
            {
                _started = true;

                // 1) Scan LSN with a temporary reader
                if (File.Exists(_path))
                {
                    using var reader = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        4096,
                        useAsync: true);

                    var hdr = new byte[12];
                    long last = 0;
                    while (reader.Position < reader.Length)
                    {
                        int n = await reader.ReadAsync(hdr, 0, hdr.Length, ct).ConfigureAwait(false);
                        if (n < hdr.Length) break;
                        long lsn = BinaryPrimitives.ReadInt64LittleEndian(hdr);
                        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8, 4));
                        last = Math.Max(last, lsn);
                        reader.Seek(len, SeekOrigin.Current);
                    }
                    Interlocked.Exchange(ref _lsnCounter, last);
                }

                // 2) Now open your writer-only stream at end
                _stream = new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    useAsync: true);
                _stream.Seek(0, SeekOrigin.End);
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(WalMetrics.StartCount.Name);
                _metrics.Record(WalMetrics.StartDuration.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async Task AppendAsync(WalRecord record, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started)
                throw new InvalidOperationException("WAL not started.");

            _metrics.Add(WalMetrics.InFlightOps.Name, 1);

            // 1) Allocate a new sequence number
            long lsn = Interlocked.Increment(ref _lsnCounter);

            // 2) Prepare the 12-byte header
            byte[] header = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, 8), lsn);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), record.Payload.Length);

            var waitSw = Stopwatch.StartNew();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSw.Stop();

            try
            {
                _metrics.Record(WalMetrics.LockWaitTime.Name, waitSw.Elapsed.TotalMilliseconds);

                // 4) Combine header + payload into one contiguous buffer
                var payload = record.Payload;
                byte[] buffer = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
                payload.CopyTo(buffer.AsMemory(header.Length));

                // 5) Single WriteAsync call (I/O timing only)
                var ioSw = Stopwatch.StartNew();
                await _stream!
                    .WriteAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                ioSw.Stop();

                _metrics.Record(WalMetrics.AppendDuration.Name, ioSw.Elapsed.TotalMilliseconds);
                _metrics.Increment(WalMetrics.AppendCount.Name);
                _metrics.Record(WalMetrics.BytesAppended.Name, buffer.Length);
            }
            finally
            {
                _lock.Release();
                _metrics.Add(WalMetrics.InFlightOps.Name, -1);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");

            _metrics.Add(WalMetrics.InFlightOps.Name, 1);

            var waitSw = Stopwatch.StartNew();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSw.Stop();

            try
            {
                _metrics.Record(WalMetrics.LockWaitTime.Name, waitSw.Elapsed.TotalMilliseconds);

                var ioSw = Stopwatch.StartNew();
                await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
                ioSw.Stop();

                _metrics.Increment(WalMetrics.FlushCount.Name);
                _metrics.Record(WalMetrics.FlushDuration.Name, ioSw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                _lock.Release();
                _metrics.Add(WalMetrics.InFlightOps.Name, -1);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var sw = Stopwatch.StartNew();
            try
            {
                if (!_started) return;

                await FlushAsync(cancellationToken).ConfigureAwait(false);
                _started = false;
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(WalMetrics.StopCount.Name);
                _metrics.Record(WalMetrics.StopDuration.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            await StopAsync(CancellationToken.None).ConfigureAwait(false);

            _stream?.Dispose();
            _lock.Dispose();
            _disposed = true;
        }

        public void Dispose() =>
            DisposeAsync().AsTask().GetAwaiter().GetResult();

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalManager));
        }

        public async Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");
            long lsn = Interlocked.Read(ref _lsnCounter);
            return await Task.FromResult(lsn).ConfigureAwait(false);
        }
    }
}
