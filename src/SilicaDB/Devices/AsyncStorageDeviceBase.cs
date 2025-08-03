using SilicaDB.Devices.Interfaces;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Devices
{
    /// <summary>
    /// Base class implementing async mount/unmount, frame‐level locking, disposal hygiene.
    /// </summary>
    public abstract class AsyncStorageDeviceBase : IStorageDevice
    {
        public const int FrameSize = 8192;

        private readonly ConcurrentDictionary<long, SemaphoreSlim> _frameLocks = new();
        private readonly object _mountSync = new();
        private bool _mounted;

        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            lock (_mountSync)
            {
                if (_mounted) throw new InvalidOperationException("Already mounted");
                _mounted = true;
            }

            await OnMountAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            lock (_mountSync)
            {
                if (!_mounted) throw new InvalidOperationException("Not mounted");
                _mounted = false;
            }

            await OnUnmountAsync(cancellationToken).ConfigureAwait(false);

            // Dispose all frame semaphores
            foreach (var kv in _frameLocks)
            {
                kv.Value.Dispose();
            }
            _frameLocks.Clear();
        }

        public async Task<byte[]> ReadFrameAsync(long frameId, CancellationToken cancellationToken = default)
        {
            EnsureMounted();
            var sem = _frameLocks.GetOrAdd(frameId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await ReadFrameInternalAsync(frameId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task WriteFrameAsync(long frameId, byte[] data, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length != FrameSize)
                throw new ArgumentException($"Buffer must be exactly {FrameSize} bytes", nameof(data));

            EnsureMounted();
            var sem = _frameLocks.GetOrAdd(frameId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await WriteFrameInternalAsync(frameId, data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        private void EnsureMounted()
        {
            lock (_mountSync)
            {
                if (!_mounted) throw new InvalidOperationException("Device is not mounted");
            }
        }

        public async ValueTask DisposeAsync()
        {
            var didUnmount = false;

            // Try to unmount if still mounted; swallow the exception otherwise
            try
            {
                await UnmountAsync().ConfigureAwait(false);
                didUnmount = true;
            }
            catch (InvalidOperationException)
            {
                // already unmounted, ignore
            }

            // If UnmountAsync never ran its cleanup (because it threw),
            // we must still dispose all per-frame semaphores ourselves.
            if (!didUnmount)
            {
                foreach (var kv in _frameLocks)
                {
                    kv.Value.Dispose();
                }
                _frameLocks.Clear();
            }
        }
        /// <summary>
        /// Override to open streams, files, etc.
        /// </summary>
        protected abstract Task OnMountAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Override to close and dispose underlying resources.
        /// </summary>
        protected abstract Task OnUnmountAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Read exactly FrameSize bytes from underlying medium.
        /// </summary>
        protected abstract Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken);

        /// <summary>
        /// Write exactly FrameSize bytes into underlying medium.
        /// </summary>
        protected abstract Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken);
    }
}
