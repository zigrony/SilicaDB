using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Decorators
{
    /// <summary>
    /// Common decorator base that forwards I/O by default.
    /// </summary>
    public abstract class StorageDeviceDecorator : IStorageDevice, IMountableStorage
    {
        protected readonly IStorageDevice Inner;

        protected StorageDeviceDecorator(IStorageDevice inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>
        /// Exposes the wrapped device for explicit, reflection-free traversal in tests/tools.
        /// </summary>
        public IStorageDevice InnerDevice => Inner;

        public virtual StorageGeometry Geometry => Inner.Geometry;

        public virtual ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            => Inner.ReadAsync(offset, buffer, token);
        public virtual ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
            => Inner.WriteAsync(offset, data, token);
        public virtual ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
            => Inner.ReadFrameAsync(frameId, buffer, token);
        public virtual ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
            => Inner.WriteFrameAsync(frameId, data, token);
        public virtual ValueTask FlushAsync(CancellationToken token = default)
            => Inner.FlushAsync(token);
        public virtual async ValueTask DisposeAsync() => await Inner.DisposeAsync().ConfigureAwait(false);
        public virtual async Task MountAsync(CancellationToken cancellationToken = default)
        {
            if (Inner is IMountableStorage m) await m.MountAsync(cancellationToken).ConfigureAwait(false);
        }
        public virtual async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            if (Inner is IMountableStorage m) await m.UnmountAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
