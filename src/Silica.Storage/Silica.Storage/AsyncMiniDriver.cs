// Filename: \Silica.Storage\AsyncMiniDriver.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Silica.Common;
using Silica.Evictions;
using Silica.Observability.Metrics;
using Silica.Observability.Metrics.Interfaces;
using Silica.Observability.Tracing;
using Silica.Storage.Interfaces;

// Disambiguate Trace
using Trace = Silica.Observability.Tracing.Trace;

namespace Silica.Storage
{
    /// <summary>
    /// Implements:
    ///  • async mount/unmount lifecycle with drain-on-unmount
    ///  • eviction-aware, per-frame semaphore locks tracked in a cache
    ///  • frame-granular I/O (ReadFrameAsync/WriteFrameAsync) with tracing & metrics
    ///  • offset/length I/O (ReadAsync/WriteAsync) with range locking & read-modify-write
    ///  • proper disposal hygiene
    /// </summary>
    public abstract class AsyncMiniDriver : IStorageDevice
    {
        // --- Interface to implement in concrete subclasses ---
        public abstract StorageGeometry Geometry { get; }

        /// <summary>
        /// Called inside MountAsync. Open streams, files, ... here.
        /// </summary>
        protected abstract Task OnMountAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Called inside UnmountAsync. Close/dispose underlying resources.
        /// </summary>
        protected abstract Task OnUnmountAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Hook for FlushAsync; default no-op.
        /// </summary>
        protected virtual Task FlushAsyncInternal(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Concrete devices must read exactly one frame into the provided buffer.
        /// </summary>
        protected abstract Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken);

        /// <summary>
        /// Concrete devices must write exactly one frame from the provided data.
        /// </summary>
        protected abstract Task WriteFrameInternalAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken);

        // --- A tiny wrapper around SemaphoreSlim to track holders/waiters and detect idle for eviction ---
        private sealed class FrameLock
        {
            public readonly SemaphoreSlim Sem = new SemaphoreSlim(1, 1);
            int _holders, _waiters;

            public int HolderCount => Volatile.Read(ref _holders);
            public int WaiterCount => Volatile.Read(ref _waiters);

            // True only when nobody holds or waits
            public bool IsIdle => HolderCount == 0 && WaiterCount == 0;

            public void MarkWaiting() => Interlocked.Increment(ref _waiters);
            public void MarkWaitComplete() => Interlocked.Decrement(ref _waiters);
            public void MarkAcquired() => Interlocked.Increment(ref _holders);
            public void MarkReleased() => Interlocked.Decrement(ref _holders);

            private int _closed; // 0 = open, 1 = closed
            public bool IsClosed => Volatile.Read(ref _closed) == 1;
            public void Close() => Interlocked.Exchange(ref _closed, 1);

            /// <summary>
            /// Atomically mark that we will wait, guard against racing eviction,
            /// then wait on the semaphore—always decrement waiters on exit.
            /// </summary>
            public async ValueTask WaitAsyncSafely(CancellationToken cancellationToken)
            {
                MarkWaiting();

                if (IsClosed)
                {
                    MarkWaitComplete();
                    throw new ObjectDisposedException(nameof(FrameLock));
                }

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

        // --- Fields for metrics, eviction, mount state, in-flight tracking ---
        private readonly IMetricsManager _metrics;
        private readonly EvictionManager _evictionManager;
        private readonly EvictionTimeCache<long, FrameLock> _frameLockCache;

        private readonly object _stateLock = new();
        private bool _mounted;
        private CancellationTokenSource _lifecycleCts = new();
        private int _inFlightCount;
        private TaskCompletionSource<object> _drainTcs = null!;

        /// <summary>
        /// Construct with custom metrics manager & eviction parameters.
        /// </summary>
        protected AsyncMiniDriver(
            IMetricsManager metrics,
            EvictionManager? evictionManager = null,
            TimeSpan? evictionInterval = null,
            TimeSpan? frameLockIdleTimeout = null)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

            // 1) Create or accept an eviction manager
            var interval = evictionInterval ?? TimeSpan.FromMinutes(10);
            _evictionManager = evictionManager ?? new EvictionManager(interval);

            // 2) FrameLock cache with idle TTL
            var ttl = frameLockIdleTimeout ?? TimeSpan.FromMinutes(10);
            _frameLockCache = new EvictionTimeCache<long, FrameLock>(
                idleTimeout: ttl,
                factory: _ => new ValueTask<FrameLock>(new FrameLock()),
                onEvictedAsync: (frameId, fl) =>
                {
                    fl.Close(); // reject new waiters deterministically
                    if (fl.IsIdle)
                        _metrics.Increment(StorageMetrics.EvictionCount.Name);
                    return ValueTask.CompletedTask;
                });


            _evictionManager.RegisterCache(_frameLockCache);

            // 3) Wire up all storage metrics
            StorageMetrics.RegisterAll(
                _metrics,
                deviceName: GetType().Name,
                cacheSizeProvider: () => _frameLockCache.Count);
        }

        /// <summary>
        /// Default ctor uses a built-in MetricsManager and default eviction.
        /// </summary>
        protected AsyncMiniDriver()
            : this(new MetricsManager(), evictionManager: null)
        { }

        //--- MOUNT / UNMOUNT / DISPOSE ---

        /// <summary>
        /// Mounts the device, runs OnMountAsync, tracks metrics.
        /// </summary>
        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            await using var scope = Trace.AsyncScope(
                TraceCategory.Device,
                "MountAsync",
                GetType().Name);

            var sw = Stopwatch.StartNew();
            lock (_stateLock)
            {
                if (_mounted) throw new InvalidOperationException("Already mounted");
                _mounted = true;
                _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                await OnMountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // roll back mount flag so future attempts can retry
                lock (_stateLock)
                {
                    _mounted = false;
                    _drainTcs = null!;
                }
                throw;
            }
            finally
            {
                sw.Stop();
                _metrics.Increment(StorageMetrics.MountCount.Name);
                _metrics.Record(StorageMetrics.MountDuration.Name, sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Cancels new I/O, waits for in-flight to drain, runs OnUnmountAsync, tracks metrics.
        /// </summary>
        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            await using var scope = Trace.AsyncScope(
                TraceCategory.Device,
                "UnmountAsync",
                GetType().Name);

            var sw = Stopwatch.StartNew();
            lock (_stateLock)
            {
                if (!_mounted) return;
                _mounted = false;
            }

            // stop new operations
            _lifecycleCts.Cancel();

            // wait for existing operations to finish
            Task drainWait;
            lock (_stateLock)
                drainWait = _inFlightCount == 0
                    ? Task.CompletedTask
                    : _drainTcs.Task;

            await drainWait.ConfigureAwait(false);

            // unregister eviction cache and unmount
            _evictionManager.UnregisterCache(_frameLockCache);
            await OnUnmountAsync(cancellationToken).ConfigureAwait(false);

            sw.Stop();
            _metrics.Increment(StorageMetrics.UnmountCount.Name);
            _metrics.Record(StorageMetrics.UnmountDuration.Name, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// Disposes the device: tries UnmountAsync, then tears down eviction + CTS, preserves original exception.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Exception? unmountEx = null;
            try
            {
                await UnmountAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                unmountEx = ex;
            }

            try
            {
                _evictionManager.UnregisterCache(_frameLockCache);
                _evictionManager.Dispose();
                await _frameLockCache.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cacheEx)
            {
                if (unmountEx is not null)
                    throw new AggregateException(unmountEx, cacheEx);
                throw;
            }
            finally
            {
                _lifecycleCts.Dispose();
            }

            if (unmountEx is not null)
                ExceptionDispatchInfo.Capture(unmountEx).Throw();
        }

        //--- FRAME-GRANULAR I/O (IStorageDevice.ReadFrameAsync / WriteFrameAsync) ---

        public async ValueTask<int> ReadFrameAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await using var scope = Trace.AsyncScope(
                TraceCategory.Device,
                "ReadFrameAsync",
                GetType().Name);

            EnsureMounted();

            // 1) Grab or create the per-frame lock
            var fl = await _frameLockCache.GetOrAddAsync(frameId).ConfigureAwait(false);

            // 2) Track in-flight
            _metrics.Add(StorageMetrics.InFlightOps.Name, 1);

            // 3) Combine caller + unmount cancellation
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, _lifecycleCts.Token);

            var lockSw = Stopwatch.StartNew();
            bool acquired = false;

            try
            {
                // 4) Wait safely (tracks waiters, guards eviction)
                await fl.WaitAsyncSafely(linked.Token).ConfigureAwait(false);
                acquired = true;

                lockSw.Stop();
                _metrics.Record(StorageMetrics.LockWaitTime.Name, lockSw.Elapsed.TotalMilliseconds);

                // 5) Mark acquired & bump in-flight count
                fl.MarkAcquired();
                lock (_stateLock) { _inFlightCount++; }

                // 6) Do the real I/O
                var ioSw = Stopwatch.StartNew();
                await ReadFrameInternalAsync(frameId, buffer, linked.Token).ConfigureAwait(false);
                ioSw.Stop();

                // 7) Record read metrics
                _metrics.Record(StorageMetrics.ReadLatency.Name, ioSw.Elapsed.TotalMilliseconds);
                _metrics.Increment(StorageMetrics.ReadCount.Name);
                _metrics.Record(StorageMetrics.BytesRead.Name, buffer.Length);

                return buffer.Length;
            }
            finally
            {
                if (acquired)
                {
                    fl.Sem.Release();
                    fl.MarkReleased();
                }

                // 8) Decrement in-flight and signal drain if unmounted
                TaskCompletionSource<object>? toDrain = null;
                lock (_stateLock)
                {
                    if (_inFlightCount > 0) _inFlightCount--;
                    if (_inFlightCount == 0 && !_mounted)
                        toDrain = _drainTcs;
                }
                toDrain?.TrySetResult(null);
                _metrics.Add(StorageMetrics.InFlightOps.Name, -1);
            }
        }

        public async ValueTask WriteFrameAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            await using var scope = Trace.AsyncScope(
                TraceCategory.Device,
                "WriteFrameAsync",
                GetType().Name);

            EnsureMounted();

            if (data.Length != Geometry.LogicalBlockSize)
                throw new ArgumentException(
                    $"Frame I/O requires exactly {Geometry.LogicalBlockSize} bytes",
                    nameof(data));

            var fl = await _frameLockCache.GetOrAddAsync(frameId).ConfigureAwait(false);
            _metrics.Add(StorageMetrics.InFlightOps.Name, 1);

            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, _lifecycleCts.Token);

            var lockSw = Stopwatch.StartNew();
            bool acquired = false;

            try
            {
                await fl.WaitAsyncSafely(linked.Token).ConfigureAwait(false);
                acquired = true;

                lockSw.Stop();
                _metrics.Record(StorageMetrics.LockWaitTime.Name, lockSw.Elapsed.TotalMilliseconds);

                fl.MarkAcquired();
                lock (_stateLock) { _inFlightCount++; }

                var ioSw = Stopwatch.StartNew();
                await WriteFrameInternalAsync(frameId, data, linked.Token).ConfigureAwait(false);
                ioSw.Stop();

                _metrics.Record(StorageMetrics.WriteLatency.Name, ioSw.Elapsed.TotalMilliseconds);
                _metrics.Increment(StorageMetrics.WriteCount.Name);
                _metrics.Record(StorageMetrics.BytesWritten.Name, data.Length);
            }
            finally
            {
                if (acquired)
                {
                    fl.Sem.Release();
                    fl.MarkReleased();
                }

                TaskCompletionSource<object>? toSignal = null;
                lock (_stateLock)
                {
                    if (_inFlightCount > 0) _inFlightCount--;
                    if (_inFlightCount == 0 && !_mounted)
                        toSignal = _drainTcs;
                }
                toSignal?.TrySetResult(null);
                _metrics.Add(StorageMetrics.InFlightOps.Name, -1);
            }
        }

        //--- OFFSET/LENGTH I/O (IStorageDevice.ReadAsync / WriteAsync) ---

        public async ValueTask<int> ReadAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureMounted();

            var frameSize = Geometry.LogicalBlockSize;
            var frameIds = GetFrameRange(offset, buffer.Length, frameSize);
            var locks = await AcquireFrameLocksAsync(frameIds, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await ReadUnlockedAsync(offset, buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseFrameLocks(locks);
            }
        }

        public async ValueTask WriteAsync(
            long offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            EnsureMounted();

            var frameSize = Geometry.LogicalBlockSize;
            var frameIds = GetFrameRange(offset, data.Length, frameSize);
            var locks = await AcquireFrameLocksAsync(frameIds, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await WriteUnlockedAsync(offset, data, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseFrameLocks(locks);
            }
        }

        /// <summary>
        /// Exposes FlushAsync to the interface; delegates to FlushAsyncInternal.
        /// </summary>
        public virtual ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => FlushAsyncInternal(cancellationToken).AsValueTask();

        //--- Private helpers for offset/length logic ---

        private async ValueTask<int> ReadUnlockedAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var frameSize = Geometry.LogicalBlockSize;
            var inner = (int)(offset % frameSize);

            // fast-path single full frame
            if (inner == 0 && buffer.Length == frameSize)
            {
                await ReadFrameAsync(offset / frameSize, buffer, cancellationToken)
                    .ConfigureAwait(false);
                return frameSize;
            }

            int total = 0;
            var temp = new byte[frameSize];
            long current = offset;
            int remaining = buffer.Length;

            while (remaining > 0)
            {
                var frameId = current / frameSize;
                var fo = (int)(current % frameSize);
                var take = Math.Min(frameSize - fo, remaining);

                await ReadFrameAsync(frameId, temp, cancellationToken)
                    .ConfigureAwait(false);

                new Span<byte>(temp, fo, take)
                    .CopyTo(buffer.Span.Slice(total, take));

                total += take;
                current += take;
                remaining -= take;
            }

            return total;
        }

        private async ValueTask WriteUnlockedAsync(
            long offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            var frameSize = Geometry.LogicalBlockSize;
            var inner = (int)(offset % frameSize);

            // fast-path single full frame
            if (inner == 0 && data.Length == frameSize)
            {
                await WriteFrameAsync(offset / frameSize, data, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            long current = offset;
            int remaining = data.Length;
            var temp = new byte[frameSize];
            int srcIdx = 0;

            while (remaining > 0)
            {
                var frameId = current / frameSize;
                var fo = (int)(current % frameSize);
                var put = Math.Min(frameSize - fo, remaining);

                // read-modify-write
                await ReadFrameAsync(frameId, temp, cancellationToken)
                    .ConfigureAwait(false);

                data.Slice(srcIdx, put).Span
                    .CopyTo(temp.AsSpan(fo, put));

                await WriteFrameAsync(frameId, temp, cancellationToken)
                    .ConfigureAwait(false);

                current += put;
                srcIdx += put;
                remaining -= put;
            }
        }

        private static List<long> GetFrameRange(
            long offset,
            int length,
            int frameSize)
        {
            long first = offset / frameSize;
            long last = (offset + length - 1) / frameSize;
            var list = new List<long>((int)(last - first + 1));
            for (long i = first; i <= last; i++)
                list.Add(i);
            return list;
        }

        private async ValueTask<List<FrameLock>> AcquireFrameLocksAsync(
            IEnumerable<long> frameIds,
            CancellationToken cancellationToken)
        {
            var locks = new List<FrameLock>();
            try
            {
                foreach (var id in frameIds.OrderBy(x => x))
                {
                    var fl = await _frameLockCache.GetOrAddAsync(id).ConfigureAwait(false);

                    await fl.WaitAsyncSafely(cancellationToken).ConfigureAwait(false);

                    fl.MarkAcquired();
                    locks.Add(fl);
                }
                return locks;
            }
            catch
            {
                // roll back any partial acquisitions
                foreach (var fl in locks)
                {
                    fl.Sem.Release();
                    fl.MarkReleased();
                }
                throw;
            }
        }

        private static void ReleaseFrameLocks(
            IEnumerable<FrameLock> locks)
        {
            foreach (var fl in locks)
            {
                fl.Sem.Release();
                fl.MarkReleased();
            }
        }

        private void EnsureMounted()
        {
            lock (_stateLock)
            {
                if (!_mounted)
                    throw new InvalidOperationException("Device is not mounted");
            }
        }
    }

    internal static class TaskExtensions
    {
        public static ValueTask AsValueTask(this Task t) =>
            t.IsCompletedSuccessfully
                ? ValueTask.CompletedTask
                : new ValueTask(t);
    }
}
