using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Decorators
{
    /// <summary>
    /// Common decorator base that forwards I/O by default.
    /// </summary>
    public abstract class StorageDeviceDecorator : IStorageDevice, IMountableStorage, IStackManifestHost
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
            // Deterministically traverse decorator chain (no reflection) to first mountable device.
            IStorageDevice current = Inner;
            while (true)
            {
                if (current is IMountableStorage mountable)
                {
                    await mountable.MountAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (current is StorageDeviceDecorator decorator)
                {
                    current = decorator.InnerDevice;
                    continue;
                }
                break;
            }
            throw new InvalidOperationException("No mountable device found in decorator chain (IMountableStorage required at the base).");
        }
        public virtual async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            // Deterministically traverse decorator chain (no reflection) to first mountable device.
            IStorageDevice current = Inner;
            while (true)
            {
                if (current is IMountableStorage mountable)
                {
                    await mountable.UnmountAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (current is StorageDeviceDecorator decorator)
                {
                    current = decorator.InnerDevice;
                    continue;
                }
                break;
            }
            throw new InvalidOperationException("No mountable device found in decorator chain (IMountableStorage required at the base).");
        }

        // Forward manifest control to the base device when present.
        public void SetExpectedManifest(DeviceManifest manifest)
        {
            // Deterministically traverse decorator chain to the first manifest host.
            IStorageDevice current = Inner;
            while (true)
            {
                if (current is IStackManifestHost host)
                {
                    host.SetExpectedManifest(manifest);
                    return;
                }
                if (current is StorageDeviceDecorator decorator)
                {
                    current = decorator.InnerDevice;
                    continue;
                }
                break;
            }
            // If a non-host device sits at the bottom, make the failure explicit.
            throw new InvalidOperationException("No stack manifest host found in decorator chain (IStackManifestHost required at the base).");
        }
    }
}
