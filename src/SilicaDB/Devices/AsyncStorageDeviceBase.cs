using SilicaDB.Devices.Interfaces;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using SilicaDB.Evictions;
using SilicaDB.Evictions.Interfaces;
using System.Runtime.ExceptionServices;
using SilicaDB.Common;

namespace SilicaDB.Devices
{
    /// <summary>
    /// Base class implementing async mount/unmount, frame‐level locking, disposal hygiene.
    /// </summary>
    public abstract class AsyncStorageDeviceBase : IStorageDevice
    {
        // gauge showing current number of frame‐locks in the cache
        private readonly ObservableGauge<long> _cacheSizeGauge;

        // 1a) Meter for all storage events
        private static readonly Meter StorageMeter =
            new Meter("SilicaDB.Storage", "1.0");

        // 1b) Mount/unmount
        private static readonly Counter<long> MountCounter =
            StorageMeter.CreateCounter<long>("storage.mount.count");
        private static readonly Histogram<double> MountDuration =
            StorageMeter.CreateHistogram<double>("storage.mount.duration", "ms");

        private static readonly Counter<long> UnmountCounter =
            StorageMeter.CreateCounter<long>("storage.unmount.count");
        private static readonly Histogram<double> UnmountDuration =
            StorageMeter.CreateHistogram<double>("storage.unmount.duration", "ms");

        // 1c) Read/Write ops
        private static readonly Counter<long> ReadCount =
            StorageMeter.CreateCounter<long>("storage.read.count");
        private static readonly Counter<long> BytesRead =
            StorageMeter.CreateCounter<long>("storage.read.bytes", "bytes");
        private static readonly Histogram<double> ReadLatency =
            StorageMeter.CreateHistogram<double>("storage.read.duration", "ms");

        private static readonly Counter<long> WriteCount =
            StorageMeter.CreateCounter<long>("storage.write.count");
        private static readonly Counter<long> BytesWritten =
            StorageMeter.CreateCounter<long>("storage.write.bytes", "bytes");
        private static readonly Histogram<double> WriteLatency =
            StorageMeter.CreateHistogram<double>("storage.write.duration", "ms");

        // 1d) Lock-wait / in-flight
        private static readonly Histogram<double> LockWaitTime =
            StorageMeter.CreateHistogram<double>("storage.lock.wait", "ms");
        private static readonly UpDownCounter<long> InFlightOps =
            StorageMeter.CreateUpDownCounter<long>("storage.inflight", "ops");

        // eviction metrics: count how many frame‐locks get disposed
        private static readonly Counter<long> EvictionCount =
            StorageMeter.CreateCounter<long>("storage.lock.evicted.count");

        public const int FrameSize = 8192;

        // Single lock for mount state, semaphore dict and in-flight tracking
        private readonly object _stateLock = new();
        //private readonly Dictionary<long, SemaphoreSlim> _frameLocks = new();

        // 3. Add this tiny wrapper for each frame’s semaphore:
        private sealed class FrameLock
        {
        public SemaphoreSlim Sem { get; } = new SemaphoreSlim(1, 1);

        // number of callers who have successfully entered the semaphore
        private int _holders;
        // number of callers currently blocked in WaitAsync
        private int _waiters;

        // observability: expose current holder and waiter counts
        public int HolderCount => Volatile.Read(ref _holders);
        public int WaiterCount => Volatile.Read(ref _waiters);
        // Called just before awaiting WaitAsync

        public void MarkWaiting() =>
            Interlocked.Increment(ref _waiters);

        // Called immediately after WaitAsync returns or throws
        public void MarkWaitComplete() =>
            Interlocked.Decrement(ref _waiters);

        // Called after you have the semaphore
        public void MarkAcquired() =>
            Interlocked.Increment(ref _holders);

        // Called when releasing the semaphore
        public void MarkReleased() =>
            Interlocked.Decrement(ref _holders);

        // Helper for eviction: true only when nobody holds or waits
        public bool IsIdle =>
            Volatile.Read(ref _holders) == 0
            && Volatile.Read(ref _waiters) == 0;

            /// <summary>
            /// Atomically tracks this waiter, guards against eviction,
            /// then awaits the semaphore and always decrements the waiter count.
            /// </summary>
            public async ValueTask WaitAsyncSafely(CancellationToken cancellationToken)
            {
                // 1) Mark that we’re about to block
                MarkWaiting();

                // 2) Immediately guard against a racing eviction
                if (IsIdle)
                {
                    // undo our mark before throwing
                    MarkWaitComplete();
                    throw new InvalidOperationException("Lock disposed during wait");
                }

                // 3) Block on the semaphore; on exit (success, cancel or fault) decrement waiter
                try
                {
                    await Sem.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    MarkWaitComplete();
                }
            }
        }


        private readonly EvictionManager _evictionManager;
        private readonly EvictionTimeCache<long, FrameLock> _frameLockCache;
        private bool _mounted = false;

        // Broadcasts Unmount requests
        private readonly CancellationTokenSource _lifecycleCts = new();

        // Count of current Read/Write operations
        private int _inFlightCount = 0;
            private TaskCompletionSource<object> _drainTcs; // = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected AsyncStorageDeviceBase(
            EvictionManager? evictionManager = null,
            TimeSpan? evictionInterval = null,
            TimeSpan? frameLockIdleTimeout = null)
        {
            // 1. Drive eviction on the supplied interval (default 10 m)
            var interval = evictionInterval ?? TimeSpan.FromMinutes(10);
            _evictionManager = evictionManager ?? new EvictionManager(interval);

            // 2. Evict frame-locks when idle past the supplied TTL (default 10 m)
            var ttl = frameLockIdleTimeout ?? TimeSpan.FromMinutes(10);
            _frameLockCache = new EvictionTimeCache<long, FrameLock>(
                idleTimeout: ttl,
                factory: _ => new ValueTask<FrameLock>(new FrameLock()),
                onEvictedAsync: EvictFrameLock);

            // 3) Observe cache size
            _cacheSizeGauge = StorageMeter.CreateObservableGauge<long>(
                                "storage.lock.cache.size",
                                () => _frameLockCache.Count,
                                "locks");

            _evictionManager.RegisterCache(_frameLockCache);
        }

        protected AsyncStorageDeviceBase()
            : this(evictionManager: null)
        { }

        // Eviction callback
        // Eviction callback – only dispose when no holders remain
        private ValueTask EvictFrameLock(long frameId, FrameLock frameLock)
        {
            if (frameLock.IsIdle)
            {
                EvictionCount.Add(1, ("device", GetType().Name));
                Console.WriteLine(
                    $"[Eviction] Disposing frame lock for {frameId} at {DateTime.Now:HH:mm:ss.fff}");

                // free the underlying SemaphoreSlim handle
                frameLock.Sem.Dispose();
            }
            else
            {
                Console.WriteLine(
                    $"[Eviction] Skipped disposing frame lock {frameId}, still in use.");
            }

            return ValueTask.CompletedTask;
        }

        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            MountCounter.Add(1, ("device", GetType().Name));
            try
            {
                lock (_stateLock)
                {
                    if (_mounted)
                        throw new InvalidOperationException("Already mounted");
                    _mounted = true;
                    _drainTcs = new TaskCompletionSource<object>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                try
                {
                    await OnMountAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Roll back mount flag so future MountAsync can retry
                    lock (_stateLock)
                    {
                        _mounted = false;
                        // Optionally clear _drainTcs to avoid stale Task
                        _drainTcs = null!;
                    }
                    throw;
                    }
                }
            finally
            {
                sw.Stop();
                MountDuration.Record(sw.Elapsed.TotalMilliseconds,("device", GetType().Name));
            }
        }


        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            UnmountCounter.Add(1, ("device", GetType().Name));
            try
            {
                lock (_stateLock)
                {
                    // idempotent: if already unmounted, just exit
                    if (!_mounted)
                        return;

                    _mounted = false;
                }

                _lifecycleCts.Cancel();

                Task waitForDrain;
                lock (_stateLock)
                {
                    waitForDrain = _inFlightCount == 0
                        ? Task.CompletedTask
                        : _drainTcs.Task;
                }
                await waitForDrain.ConfigureAwait(false);

                // unregister and let the eviction manager drain and dispose everything
                _evictionManager.UnregisterCache(_frameLockCache);
                await OnUnmountAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                UnmountDuration.Record(sw.Elapsed.TotalMilliseconds,("device", GetType().Name));
            }
        }

        public async Task<byte[]> ReadFrameAsync(long frameId, CancellationToken cancellationToken = default)
        {
            EnsureMounted();

            // 1) Grab or create the per-frame lock
            var frameLock = await _frameLockCache.GetOrAddAsync(frameId).ConfigureAwait(false);

            // 2) Track the operation in-flight
            InFlightOps.Add(1, ("device", GetType().Name));

            // 3) Combine unmount- and caller-cancellation
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);

            // 4) Time semaphore wait
            var lockSw = Stopwatch.StartNew();
            bool acquired = false;
            byte[] result;

            try
            {
                // 5) Wait on the frame semaphore safely (tracks waiters, checks eviction)
                await frameLock.WaitAsyncSafely(linkedCts.Token).ConfigureAwait(false);

                acquired = true;

                // 6) Record lock-wait metric
                lockSw.Stop();
                LockWaitTime.Record(lockSw.Elapsed.TotalMilliseconds,("device", GetType().Name));

                // 7) Book-keep the acquisition
                frameLock.MarkAcquired();
                lock (_stateLock)
                {
                    _inFlightCount++;
                }

                // 8) Do the real I/O
                var ioSw = Stopwatch.StartNew();
                result = await ReadFrameInternalAsync(frameId, linkedCts.Token)
                                      .ConfigureAwait(false);
                ioSw.Stop();

                // 9) Record read metrics
                ReadLatency.Record(ioSw.Elapsed.TotalMilliseconds, ("device", GetType().Name), ("operation", "read"));
                ReadCount.Add(1, ("device", GetType().Name), ("operation", "read"));
                BytesRead.Add(result.Length, ("device", GetType().Name), ("operation", "read"));
                return result;
            }
            finally
            {
                // 10) Release only if we got the lock
                if (acquired)
                {
                    frameLock.Sem.Release();
                    frameLock.MarkReleased();
                }

                // 11) Decrement in-flight and signal drain if unmounted
                TaskCompletionSource<object>? toDrain = null;
                lock (_stateLock)
                {
                    if (_inFlightCount > 0)
                        _inFlightCount--;
                    if (_inFlightCount == 0 && !_mounted)
                        toDrain = _drainTcs;
                }
                toDrain?.TrySetResult(null);

                // 12) Mark the operation complete
                InFlightOps.Add(-1, ("device", GetType().Name));
            }
        }

        public async Task WriteFrameAsync(long frameId, byte[] data, CancellationToken cancellationToken = default)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length != FrameSize)
                throw new ArgumentException($"Buffer must be exactly {FrameSize} bytes", nameof(data));
            EnsureMounted();

            // 1) Grab or create the per-frame lock
            var frameLock = await _frameLockCache
                .GetOrAddAsync(frameId)
                .ConfigureAwait(false);

            // 2) Track this operation in-flight (metrics)
            InFlightOps.Add(1, ("device", GetType().Name));


            // 3) Combine unmount- and caller’s cancellation
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);

            // 4) Prepare timings
            var lockSw = Stopwatch.StartNew();
            var ioSw = new Stopwatch();
            bool acquired = false;

            try
            {
                // 5) Wait on the frame semaphore safely (tracks waiters, checks eviction)
                await frameLock.WaitAsyncSafely(linkedCts.Token).ConfigureAwait(false);

                acquired = true;

                // 6) Record how long we waited for the lock
                lockSw.Stop();
                LockWaitTime.Record(lockSw.Elapsed.TotalMilliseconds, ("device", GetType().Name));

                // 7) Book-keep the acquisition
                frameLock.MarkAcquired();
                lock (_stateLock)
                {
                    _inFlightCount++;
                }

                // 8) Perform the actual write
                ioSw.Start();
                await WriteFrameInternalAsync(frameId, data, linkedCts.Token)
                    .ConfigureAwait(false);
                ioSw.Stop();

                // 9) Record write metrics
                WriteLatency.Record(ioSw.Elapsed.TotalMilliseconds, ("device", GetType().Name), ("operation", "write"));
                WriteCount.Add(1, ("device", GetType().Name), ("operation", "write"));
                BytesWritten.Add(data.Length, ("device", GetType().Name), ("operation", "write"));
            }
            finally
            {
                // 10) Release only if we acquired the semaphore
                if (acquired)
                {
                    frameLock.Sem.Release();
                    frameLock.MarkReleased();
                }

                // 11) Decrement in-flight and signal drain if unmounted
                TaskCompletionSource<object>? toSignal = null;
                lock (_stateLock)
                {
                    if (_inFlightCount > 0)
                        _inFlightCount--;
                    if (_inFlightCount == 0 && !_mounted)
                        toSignal = _drainTcs;
                }
                toSignal?.TrySetResult(null);

                // 12) Complete the in-flight metric
                InFlightOps.Add(-1, ("device", GetType().Name));
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
            Exception? unmountEx = null;

            // 1) Try to unmount (ignore only the "already unmounted" case)
            try
            {
                await UnmountAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                unmountEx = ex;
            }

            // 2) Teardown eviction / cache
            try
            {
                _evictionManager.UnregisterCache(_frameLockCache);
                await _frameLockCache.DisposeAsync().ConfigureAwait(false);

                // if you created the manager yourself, shut it down:
                // await _evictionManager.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cacheEx)
            {
                // If you care about cache-dispose failures, you could
                // wrap it with the unmountEx in an AggregateException.
                if (unmountEx is not null)
                    throw new AggregateException(unmountEx, cacheEx);
                else
                    throw;
            }

            // 3) Dispose our own CTS
            _lifecycleCts.Dispose();

            // 4) Re-throw the original unmount exception (if any), preserving its stack
            if (unmountEx is not null)
                ExceptionDispatchInfo.Capture(unmountEx).Throw();
        }
        /// <summary>
        /// Override to open streams, files, etc.
        /// </summary>
        protected abstract Task OnMountAsync(CancellationToken cancellationToken);
        protected abstract Task OnUnmountAsync(CancellationToken cancellationToken);
        protected abstract Task<byte[]> ReadFrameInternalAsync(
                                            long frameId,
                                            CancellationToken cancellationToken);
        protected abstract Task WriteFrameInternalAsync(
                                            long frameId,
                                            byte[] buffer,
                                            CancellationToken cancellationToken);


    }
}
