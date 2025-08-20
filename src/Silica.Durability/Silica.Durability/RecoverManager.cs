using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Durability
{
    /// <summary>
    /// Single-consumer, async-only WAL replay manager.
    /// Serializes concurrent reads via SemaphoreSlim.
    /// </summary>
    public sealed class RecoverManager : IRecoverManager
    {
        private readonly string _path;
        private FileStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _started;
        private bool _disposed;

        public RecoverManager(string walFilePath)
        {
            _path = walFilePath ?? throw new ArgumentNullException(nameof(walFilePath));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("RecoverManager already started.");
            _started = true;

            _stream = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true
            );

            _stream.Seek(0, SeekOrigin.Begin);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task<WalRecord?> ReadNextAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) throw new InvalidOperationException("RecoverManager not started.");

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stream!.Position >= _stream.Length)
                    return null;

                // Read fixed-size header (8-byte LSN + 4-byte length)
                var headerBuffer = new byte[12];
                int hr = await _stream
                    .ReadAsync(headerBuffer, 0, headerBuffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (hr < headerBuffer.Length)
                    throw new IOException("Truncated WAL header.");

                long lsn = BinaryPrimitives.ReadInt64LittleEndian(headerBuffer.AsSpan(0, 8));
                int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(8, 4));

                // Read payload
                var payload = new byte[payloadLen];
                int pr = await _stream
                    .ReadAsync(payload, 0, payloadLen, cancellationToken)
                    .ConfigureAwait(false);
                if (pr != payloadLen)
                    throw new IOException("Truncated WAL payload.");

                return new WalRecord(lsn, payload);
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started) return Task.CompletedTask;
            _started = false;
            return Task.CompletedTask;
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
                throw new ObjectDisposedException(nameof(RecoverManager));
        }
    }
}
