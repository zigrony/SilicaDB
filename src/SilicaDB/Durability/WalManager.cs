using System;
using System.IO;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Durability
{
    public sealed class WalManager : IWalManager
    {
        private readonly string _path;
        private FileStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _lsnCounter;
        private bool _started;
        private bool _disposed;

        public WalManager(string logFilePath)
        {
            _path = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
        }

        public async Task StartAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("Already started");
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

        public async Task AppendAsync(WalRecord record, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started)
                throw new InvalidOperationException("WAL not started.");

            // 1) Allocate a new sequence number
            long lsn = Interlocked.Increment(ref _lsnCounter);

            // 2) Prepare the 12‐byte header
            byte[] header = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, 8), lsn);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), record.Payload.Length);

            // 3) Grab the write lock to serialize against other appends
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 4) Combine header + payload into one contiguous buffer
                var payload = record.Payload;
                byte[] buffer = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
                payload.CopyTo(buffer.AsMemory(header.Length));

                // 5) Single WriteAsync call
                await _stream!
                    .WriteAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
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

            await FlushAsync(cancellationToken).ConfigureAwait(false);
            _started = false;
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
        // File: WalManager.cs  (add this at the bottom of the class)
        public async Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("WAL not started.");
            // _lsnCounter is the field that we increment on each AppendAsync
            long lsn = Interlocked.Read(ref _lsnCounter);
            return await Task.FromResult(lsn).ConfigureAwait(false);
        }

    }
}
