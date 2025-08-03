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

    // Single lock for mount state, semaphore dict and in-flight tracking
    private readonly object _stateLock = new();
    private readonly Dictionary<long, SemaphoreSlim> _frameLocks = new();
    private bool _mounted = false;

    // Broadcasts Unmount requests
    private readonly CancellationTokenSource _lifecycleCts = new();

    // Count of current Read/Write operations
    private int _inFlightCount = 0;
        private TaskCompletionSource<object> _drainTcs; // = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock)
            {
                if (_mounted)
                    throw new InvalidOperationException("Already mounted");

                _mounted = true;

                // Reset the drain TCS for this mount/unmount cycle
                _drainTcs = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await OnMountAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock)
            {
                if (!_mounted) throw new InvalidOperationException("Not mounted");
                _mounted = false;
            }


            // 1) Tell all in-flight ops to cancel
            _lifecycleCts.Cancel();

            // 2) Wait for any in-flight operations to finish
            Task waitForDrain;
            lock (_stateLock)
            {
                waitForDrain = _inFlightCount == 0
                    ? Task.CompletedTask
                    : _drainTcs.Task;
            }
            await waitForDrain.ConfigureAwait(false);

            // 3) Now it’s safe to dispose all frame semaphores
            lock (_stateLock)
            {
                foreach (var sem in _frameLocks.Values)
                    sem.Dispose();
                _frameLocks.Clear();
            }

            await OnUnmountAsync(cancellationToken).ConfigureAwait(false);

        }

        public async Task<byte[]> ReadFrameAsync(long frameId, CancellationToken cancellationToken = default)
        {
            // 1) Snapshot mounted‐state, create/fetch semaphore, bump in-flight
            SemaphoreSlim sem;
            lock (_stateLock)
            {
                if (!_mounted)
                    throw new InvalidOperationException("Device is not mounted");

                if (!_frameLocks.TryGetValue(frameId, out sem))
                {
                    sem = new SemaphoreSlim(1, 1);
                    _frameLocks[frameId] = sem;
                }

                _inFlightCount++;
            }

            // 2) Combine user token with shutdown token
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycleCts.Token);
            var ct = linkedCts.Token;

            bool acquired = false;
            try
            {
                // 3) This will throw if lifecycleCts was canceled
                await sem.WaitAsync(ct).ConfigureAwait(false);
                acquired = true; 

                // 4) Actual I/O
                return await ReadFrameInternalAsync(frameId, ct).ConfigureAwait(false);
            }
            finally
            {
                // 5) Release the semaphore if we successfully acquired it
                if (acquired)
                    sem.Release();

                // 6) Decrement in-flight and signal drain if unmount in progress
                TaskCompletionSource<object>? toSignal = null;
                lock (_stateLock)
                {
                    _inFlightCount--;
                    if (_inFlightCount == 0 && !_mounted)
                        toSignal = _drainTcs;
                }
                toSignal?.TrySetResult(null);
            }
        }

        public async Task WriteFrameAsync(long frameId, byte[] data, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length != FrameSize)
                throw new ArgumentException($"Buffer must be exactly {FrameSize} bytes", nameof(data));

            EnsureMounted();

            SemaphoreSlim sem;
            if (!_frameLocks.TryGetValue(frameId, out sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _frameLocks[frameId] = sem;
            }

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
            lock (_stateLock)
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
