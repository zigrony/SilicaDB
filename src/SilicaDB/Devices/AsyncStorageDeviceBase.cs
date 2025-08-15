using SilicaDB.Devices.Interfaces;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using SilicaDB.Evictions;
using SilicaDB.Evictions.Interfaces;
using System.Runtime.ExceptionServices;
using SilicaDB.Common;
using SilicaDB.Metrics;
using SilicaDB.Diagnostics.Tracing;

namespace SilicaDB.Devices
{
    /// <summary>
    /// Base class implementing async mount/unmount, frame‐level locking, disposal hygiene.
    /// </summary>
    public abstract class AsyncStorageDeviceBase : IStorageDevice
    {
        private readonly IMetricsManager _metrics;
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
           IMetricsManager metrics,
           EvictionManager? evictionManager = null,
           TimeSpan? evictionInterval = null,
           TimeSpan? frameLockIdleTimeout = null)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

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
            //_cacheSizeGauge = StorageMeter.CreateObservableGauge<long>(
            //                    "storage.lock.cache.size",
            //                    () => _frameLockCache.Count,
            //                    "locks");

            _evictionManager.RegisterCache(_frameLockCache);
            // Wire up all storage metrics (counters, histograms, gauges, up-down)
            StorageMetrics.RegisterAll(
                _metrics,
                deviceName: GetType().Name,
                cacheSizeProvider: () => _frameLockCache.Count);

        }

        protected AsyncStorageDeviceBase()
            : this(new MetricsManager(), evictionManager: null)
         { }

        // Eviction callback
        // Eviction callback – only dispose when no holders remain
        private ValueTask EvictFrameLock(long frameId, FrameLock frameLock)
        {
            if (frameLock.IsIdle)
            {
                _metrics.Increment(StorageMetrics.EvictionCount.Name);
                //Console.WriteLine($"[Eviction] Disposing frame lock for {frameId} at {DateTime.Now:HH:mm:ss.fff}");

                // free the underlying SemaphoreSlim handle
                //frameLock.Sem.Dispose();
            }
            else
            {
                //Console.WriteLine($"[Eviction] Skipped disposing frame lock {frameId}, still in use.");
            }

            return ValueTask.CompletedTask;
        }

        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            // Trace the entire MountAsync operation
            await using var _traceScope = SilicaDB.Diagnostics.Tracing.Trace.AsyncScope(
            TraceCategory.Device,
            "MountAsync",
            GetType().Name);
            
            var sw = Stopwatch.StartNew();
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
                // MountCount is a Counter
                _metrics.Increment(StorageMetrics.MountCount.Name);
                // MountDuration is a Histogram
                _metrics.Record(StorageMetrics.MountDuration.Name, sw.Elapsed.TotalMilliseconds);
            }
        }


        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            // Trace the entire UnmountAsync operation
            await using var _traceScope = SilicaDB.Diagnostics.Tracing.Trace.AsyncScope(
            TraceCategory.Device,
            "UnmountAsync",
            GetType().Name);
            
            var sw = Stopwatch.StartNew(); 
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
                // UnmountCount is a Counter
                _metrics.Increment(StorageMetrics.UnmountCount.Name);
                // UnmountDuration is a Histogram
                _metrics.Record(StorageMetrics.UnmountDuration.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        public async Task<byte[]> ReadFrameAsync(long frameId, CancellationToken cancellationToken = default)
        {
            // Trace the entire ReadFrameAsync for this frameId
            await using var _traceScope = SilicaDB.Diagnostics.Tracing.Trace.AsyncScope(
            TraceCategory.Device,
            "ReadFrameAsync");
            
            EnsureMounted();
            // 1) Grab or create the per-frame lock
            var frameLock = await _frameLockCache.GetOrAddAsync(frameId).ConfigureAwait(false);

            // 2) Track the operation in-flight
            _metrics.Add(StorageMetrics.InFlightOps.Name, 1);

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
                _metrics.Record(StorageMetrics.LockWaitTime.Name, lockSw.Elapsed.TotalMilliseconds);

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
                _metrics.Record(StorageMetrics.ReadLatency.Name, ioSw.Elapsed.TotalMilliseconds);
                
                // ReadCount is a Counter
                _metrics.Increment(StorageMetrics.ReadCount.Name);
                
                // BytesRead is a Histogram
                _metrics.Record(StorageMetrics.BytesRead.Name, result.Length);
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
                _metrics.Add(StorageMetrics.InFlightOps.Name, -1);
            }
        }

        public async Task WriteFrameAsync(long frameId, byte[] data, CancellationToken cancellationToken = default)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length != FrameSize)
                throw new ArgumentException($"Buffer must be exactly {FrameSize} bytes", nameof(data));

            // Trace the entire WriteFrameAsync for this frameId
            await using var _traceScope = SilicaDB.Diagnostics.Tracing.Trace.AsyncScope(
            TraceCategory.Device,
            "WriteFrameAsync");

            // 1) Grab or create the per-frame lock
            var frameLock = await _frameLockCache
                .GetOrAddAsync(frameId)
                .ConfigureAwait(false);

            // 2) Track this operation in-flight (metrics)
            _metrics.Add(StorageMetrics.InFlightOps.Name, 1);

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
                _metrics.Record(StorageMetrics.LockWaitTime.Name, lockSw.Elapsed.TotalMilliseconds);

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
                _metrics.Record(StorageMetrics.WriteLatency.Name, ioSw.Elapsed.TotalMilliseconds);
                
                _metrics.Increment(StorageMetrics.WriteCount.Name);
                
                _metrics.Record(StorageMetrics.BytesWritten.Name, data.Length);
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
                _metrics.Add(StorageMetrics.InFlightOps.Name, -1);
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
                _evictionManager.Dispose();
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
        /// <summary>
        /// Optional durability flush. Default implementation is no-op.
        /// </summary>
        public virtual Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        protected abstract Task<byte[]> ReadFrameInternalAsync(
                                            long frameId,
                                            CancellationToken cancellationToken);
        protected abstract Task WriteFrameInternalAsync(
                                            long frameId,
                                            byte[] buffer,
                                            CancellationToken cancellationToken);

        protected void EnsureMountedOrThrow()
        {
            lock (_stateLock)
                if (!_mounted) throw new InvalidOperationException("Device is not mounted");
        }
    }
}
