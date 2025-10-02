using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage.SqlMinidriver
{
    /// <summary>
    /// Decorator that wraps an inner device and applies Sql.
    /// For now, it just forwards calls; Sql logic can be added later.
    /// </summary>
    public sealed class SqlDriver : IStorageDevice, IMountableStorage
    {
        private readonly IStorageDevice _inner;
        private readonly SqlOptions _options;

        public SqlDriver(IStorageDevice inner, SqlOptions options)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public StorageGeometry Geometry => _inner.Geometry;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(offset, buffer, ct);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _inner.WriteAsync(offset, buffer, ct);

        public ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadFrameAsync(frameId, buffer, ct);

        public ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _inner.WriteFrameAsync(frameId, buffer, ct);

        public ValueTask FlushAsync(CancellationToken ct = default)
            => _inner.FlushAsync(ct);

        public ValueTask DisposeAsync()
            => _inner.DisposeAsync();

        // --- IMountableStorage ---
        public Task MountAsync(CancellationToken cancellationToken = default)
        {
            if (_inner is IMountableStorage m)
                return m.MountAsync(cancellationToken);
            return Task.CompletedTask;
        }

        public Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            if (_inner is IMountableStorage m)
                return m.UnmountAsync(cancellationToken);
            return Task.CompletedTask;
        }
    }
}
