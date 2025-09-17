// Filename: \Silica.Storage\AsyncMiniDriver.cs

using System;
using System.Collections.Generic;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

using Silica.Common;
using Silica.Evictions;
using Silica.Storage.Interfaces;
using Silica.Storage.Exceptions;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.Storage.Metrics; // StorageMetrics
using Silica.DiagnosticsCore.Tracing; // TraceManager

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
    public abstract class AsyncMiniDriver : IStorageDevice, IMountableStorage, IStackManifestHost
    {
        private const long MetadataFrameId = 0;
        private DeviceManifest? _expectedManifest; // set by builder before MountAsync
        private bool _metadataInitialized;
        private static readonly TimeSpan s_defaultDisposeTimeout = TimeSpan.FromSeconds(30);
        private int _disposedFlag; // 0 = not disposed, 1 = disposed
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
            private int _closed; // 0 = open, 1 = closed
            private int _disposed; // 0 = not disposed, 1 = disposed (disposes Sem when closed and idle)

            public int HolderCount => Volatile.Read(ref _holders);
            public int WaiterCount => Volatile.Read(ref _waiters);

            // True only when nobody holds or waits
            public bool IsIdle => HolderCount == 0 && WaiterCount == 0;
            public bool IsClosed => Volatile.Read(ref _closed) == 1;

            public void MarkWaiting()
            {
                Interlocked.Increment(ref _waiters);
#if DEBUG
                Debug.Assert(_waiters > 0, "FrameLock._waiters underflow");
#endif
            }
            public void MarkWaitComplete()
            {
                Interlocked.Decrement(ref _waiters);
#if DEBUG
                Debug.Assert(_waiters >= 0, "FrameLock._waiters underflow");
#endif
                TryDisposeIfClosedAndIdle();
            }
            public void MarkAcquired()
            {
                Interlocked.Increment(ref _holders);
#if DEBUG
                Debug.Assert(_holders > 0, "FrameLock._holders underflow");
#endif
            }
            public void MarkReleased()
            {
                Interlocked.Decrement(ref _holders);
#if DEBUG
                Debug.Assert(_holders >= 0, "FrameLock._holders underflow");
#endif
                TryDisposeIfClosedAndIdle();
            }

            public void Close()
            {
                Interlocked.Exchange(ref _closed, 1);
                TryDisposeIfClosedAndIdle();
            }

            private void TryDisposeIfClosedAndIdle()
            {
                if (IsClosed && IsIdle)
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    {
                        try { Sem.Dispose(); } catch { /* swallow */ }
                    }
                }
            }

            /// <summary>
            /// Atomically mark that we will wait, guard against racing eviction,
            /// then wait on the semaphore. On success, mark as holder before
            /// decrementing waiters to prevent disposal-while-held races.
            /// </summary>
            public async ValueTask WaitAsyncSafely(CancellationToken cancellationToken)
            {
                MarkWaiting();

                if (IsClosed)
                {
                    MarkWaitComplete();
                    throw new FrameLockDisposedException();
                }

                try
                {
                    try
                    {
                        await Sem.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Normalize disposal to a canonical storage exception for consistent retry semantics.
                        throw new FrameLockDisposedException();
                    }
                }
                catch
                {
                    // We were a waiter but did not acquire; drop waiter count.
                    MarkWaitComplete();
                    throw;
                }

                // We now hold the semaphore. Mark as holder before dropping waiter count
                // so Close()+TryDisposeIfClosedAndIdle cannot dispose while we hold.
                MarkAcquired();
                MarkWaitComplete();

            }

        }

        // --- Fields for metrics, eviction, mount state, in-flight tracking ---
        private readonly EvictionManager _evictionManager;
        private readonly EvictionTimeCache<long, FrameLock> _frameLockCache;
        // Tunables: allow derived types to adjust without code duplication
        protected virtual int MaxFramesPerBatch => 1024;
        protected virtual int MaxLockAcquireAttempts => 8;

        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly StorageMetrics.IInFlightProvider _inFlightGauge;
        private readonly string _componentName;
        // Protected accessors for derived drivers to avoid duplicate metric managers/gauges
        protected IMetricsManager Metrics => _metrics;
        protected StorageMetrics.IInFlightProvider InFlightGauge => _inFlightGauge;
        protected string ComponentName => _componentName;
        private readonly bool _ownsEvictionManager;
        private bool _metricsRegistered;

        // Minimal, jittered sleep to reduce hot-loop pressure on repeated eviction/retries.
        // Kept very small to avoid impacting normal latency.
        private static readonly TimeSpan s_minRetryBackoff = TimeSpan.FromMilliseconds(0), s_maxRetryBackoff = TimeSpan.FromMilliseconds(2);
        protected virtual TimeSpan MinRetryBackoff => s_minRetryBackoff;
        protected virtual TimeSpan MaxRetryBackoff => s_maxRetryBackoff;

        private readonly object _stateLock = new();
        private bool _mounted;
        private CancellationTokenSource _lifecycleCts = new();
        private int _inFlightCount;
        private TaskCompletionSource<object> _drainTcs;
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private int _lifecycleGateDisposed; // 0 = not disposed, 1 = disposed

        // --- Lifecycle behavior toggles for enterprise control ---
        // If true, per-op tokens are linked with the lifecycle CTS so an unmount cancels in-flight I/O.
        // If false, operations use only the caller's token (no lifecycle cancellation), enabling graceful drain.
        protected virtual bool LinkLifecycleIntoOperations => true;

        // If true, UnmountAsync cancels the lifecycle CTS (aborting in-flight I/O).
        // If false, UnmountAsync blocks new operations but allows in-flight I/O to complete.
        protected virtual bool AbortInFlightOnUnmount => true;

        /// <summary>
        /// Best-effort mounted state for operators/tests. Not a synchronization primitive.
        /// </summary>
        public bool IsMounted
        {
            get { lock (_stateLock) { return _mounted; } }
        }


        // Helper to reduce per-op CTS allocations; resilient to concurrent lifecycle CTS disposal/replacement.
        private CancellationToken GetEffectiveToken(CancellationToken operationToken, out CancellationTokenSource? linkedCts)
        {
            // If we are not linking lifecycle into operations, just return the caller token.
            if (!LinkLifecycleIntoOperations)
            {
                linkedCts = null;
                return operationToken;
            }

            linkedCts = null;

            // If the caller token is already canceled, honor it directly without linking.
            // This preserves the caller's cancel cause and avoids per-op allocation.
            if (operationToken.IsCancellationRequested)
            {
                return operationToken;
            }

            // Retry loop only if lifecycle CTS was disposed between snapshot and Token access.
            while (true)
            {
                var life = Volatile.Read(ref _lifecycleCts);
                CancellationToken lifeToken;
                try
                {
                    lifeToken = life.Token;
                }
                catch (ObjectDisposedException)
                {
                    // Lifecycle CTS raced with dispose/replacement; retry with a fresh snapshot.
                    continue;
                }

                // If lifecycle is already canceled, prefer it (avoids allocating linked CTS).
                if (life.IsCancellationRequested)
                    return lifeToken;

                // If the caller token cannot cancel, prefer lifecycle token.
                if (!operationToken.CanBeCanceled)
                    return lifeToken;

                // If lifecycle token cannot cancel (should not happen post-construction), fall back to caller.
                if (!lifeToken.CanBeCanceled)
                    return operationToken;

                // Both can cancel and lifecycle isn't canceled yet: link once.
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken, lifeToken);
                return linkedCts.Token;
            }
        }

        /// <summary>
        /// Construct with custom metrics manager & eviction parameters.
        /// </summary>
        protected AsyncMiniDriver(
            EvictionManager? evictionManager = null,
            TimeSpan? evictionInterval = null,
            TimeSpan? frameLockIdleTimeout = null)
        {
            // 1) Create or accept an eviction manager
            var interval = (evictionInterval.HasValue && evictionInterval.Value > TimeSpan.Zero)
                ? evictionInterval.Value
                : TimeSpan.FromMinutes(10);
            if (evictionManager is null)
            {
                _evictionManager = new EvictionManager(interval);
                _ownsEvictionManager = true;
            }
            else
            {
                _evictionManager = evictionManager;
                _ownsEvictionManager = false;
            }

            // 2) FrameLock cache with idle TTL
            var ttl = (frameLockIdleTimeout.HasValue && frameLockIdleTimeout.Value > TimeSpan.Zero)
                ? frameLockIdleTimeout.Value
                : TimeSpan.FromMinutes(10);
            _frameLockCache = new EvictionTimeCache<long, FrameLock>(
                idleTimeout: ttl,
                factory: _ => new ValueTask<FrameLock>(new FrameLock()),
                onEvictedAsync: (frameId, fl) =>
                {
                    fl.Close(); // reject new waiters deterministically
                    // Account idle evictions as a storage eviction metric
                    try
                    {
                        StorageMetrics.IncrementEviction(_metrics);
                    }
                    catch { /* swallow */ }
                    return ValueTask.CompletedTask;
                });


            _evictionManager.RegisterCache(_frameLockCache);
            // 3) DiagnosticsCore metrics wiring (safe even if DiagnosticsCore isn't started yet)
            _componentName = GetType().Name;
            bool started = DiagnosticsCoreBootstrap.IsStarted;
            _metrics = started ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager();

            _inFlightGauge = StorageMetrics.CreateInFlightCounter();

            // Register the Storage metric contract with per-device tags and providers
            try
            {
                StorageMetrics.RegisterAll(
                    _metrics,
                    deviceComponentName: _componentName,
                    frameLockCacheSizeProvider: () => _frameLockCache.Count,
                    freeSpaceProvider: null,
                    queueDepthProvider: null,
                    inFlightProvider: _inFlightGauge);
                _metricsRegistered = started;
            }
            catch { /* swallow exporter/registration issues */ }
            // Initialize drain TCS in a completed state to remove null hazards before first mount.
            _drainTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainTcs.TrySetResult(null);


        }

        /// <summary>
        /// Default ctor uses a built-in MetricsManager and default eviction.
        /// </summary>
        protected AsyncMiniDriver()
            : this(null, null, null)
        { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureNotDisposed()
        {
            if (Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();
        }


        //--- MOUNT / UNMOUNT / DISPOSE ---

        /// <summary>
        /// Mounts the device, runs OnMountAsync, tracks metrics.
        /// </summary>
        public async Task MountAsync(CancellationToken cancellationToken = default)
        {
            // Ensure this instance is not disposed and serialize lifecycle transitions.
            EnsureNotDisposed();
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            bool mountSucceeded = false;
            // Prepare lifecycle CTS but do not expose as mounted until OnMountAsync has succeeded
            TaskCompletionSource<object> newDrainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource? oldCts = null;
            lock (_stateLock)
            {
                if (_mounted) throw new DeviceAlreadyMountedException();
                oldCts = _lifecycleCts;
                _lifecycleCts = new CancellationTokenSource();
                // Force re-validation of metadata on each mount of this instance.
                _metadataInitialized = false;
            }
            oldCts?.Dispose();

            // Bind metrics if constructed before bootstrap and now started
            if (!_metricsRegistered && DiagnosticsCoreBootstrap.IsStarted && _metrics is NoOpMetricsManager)
            {
                _metrics = DiagnosticsCoreBootstrap.Instance.Metrics;
                try
                {
                    StorageMetrics.RegisterAll(
                        _metrics,
                        deviceComponentName: _componentName,
                        frameLockCacheSizeProvider: () => _frameLockCache.Count,
                        freeSpaceProvider: null,
                        queueDepthProvider: null,
                        inFlightProvider: _inFlightGauge);
                    _metricsRegistered = true;
                }
                catch { /* swallow exporter/registration issues */ }
            }

            // Validate geometry before opening resources or exposing mount.
            ValidateGeometryOrThrow();
            bool resourcesOpened = false;
            try
            {
                await OnMountAsync(cancellationToken).ConfigureAwait(false);
                // At this point, underlying resources may be open. Track so we can clean up on subsequent failure.
                resourcesOpened = true;
                // Initialize/validate metadata frame now that underlying resources are open.
                await InitializeOrValidateMetadataAsync(cancellationToken).ConfigureAwait(false);
                // Only now expose as mounted and install drain TCS
                lock (_stateLock)
                {
                    _mounted = true;
                    _drainTcs = newDrainTcs;
                }
                mountSucceeded = true;
            }
            catch
            {
                // Ensure lifecycle token isn't left hanging if mount failed
                try
                {
                    _lifecycleCts.Cancel();
                }
                catch { }
                finally
                {
                    _lifecycleCts.Dispose();
                    _lifecycleCts = new CancellationTokenSource();
                }
                // If OnMountAsync succeeded but a later step failed (e.g., manifest validation),
                // tear down any resources opened during OnMountAsync to avoid leaking file handles.
                if (resourcesOpened)
                {
                    try
                    {
                        // Best-effort: do not rely on mounted state for cleanup on failed mount.
                        await OnUnmountAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow to preserve the original mount failure as the primary error.
                    }
                }
                throw;
            }
            finally
            {
                sw.Stop();
                // Record only on successful mount to keep metrics semantically true.
                if (mountSucceeded)
                {
                    try { StorageMetrics.OnMountCompleted(_metrics, sw.Elapsed.TotalMilliseconds); } catch { }
                }
                _lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Cancels new I/O, waits for in-flight to drain, runs OnUnmountAsync, tracks metrics.
        /// </summary>
        public async Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            bool wasMounted;
            bool unmountSucceeded = false;
            // Snapshot and block new operations
            lock (_stateLock)
            {
                wasMounted = _mounted;
                _mounted = false;
            }

            try
            {
                if (!wasMounted)
                {
                    // No-op unmount, still record metric duration and release gate in finally.
                    return;
                }

                // Stop new operations; optionally abort in-flight.
                if (AbortInFlightOnUnmount)
                {
                    _lifecycleCts.Cancel();
                }

                // Wait for existing operations to finish
                Task drainWait;
                lock (_stateLock)
                {
                    drainWait = _inFlightCount == 0
                        ? Task.CompletedTask
                        : _drainTcs.Task;
                }

                // Respect caller cancellation while draining (single await)
                await drainWait.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Best-effort flush after drain, before resource teardown
                try
                {
                    await FlushAsyncInternal(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Swallow flush faults on unmount to avoid masking the unmount,
                    // consistent with metrics/ops philosophy elsewhere.
                }

                // Unmount underlying resources
                await OnUnmountAsync(cancellationToken).ConfigureAwait(false);

                // Now that we've drained and torn down resources, we can safely dispose the lifecycle CTS
                // to avoid keeping a canceled CTS around while unmounted. Prepare a fresh CTS for future mounts.
                try
                {
                    _lifecycleCts.Dispose();
                }
                catch
                {
                }
                finally
                {
                    // Maintain a valid CTS object (fresh and not canceled) for subsequent effective token linking logic.
                    _lifecycleCts = new CancellationTokenSource();
                }
                // Ensure metadata is re-validated on subsequent mounts.
                _metadataInitialized = false;
                unmountSucceeded = true;
            }
            finally
            {
                sw.Stop();
                // Only emit if we actually unmounted a mounted device and completed teardown.
                if (wasMounted && unmountSucceeded)
                {
                    try { StorageMetrics.OnUnmountCompleted(_metrics, sw.Elapsed.TotalMilliseconds); } catch { }
                }
                _lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Disposes the device: tries UnmountAsync, then tears down eviction + CTS, preserves original exception.
        /// </summary>
        public virtual async ValueTask DisposeAsync()
        {
            // Idempotent dispose: first caller performs teardown, subsequent callers return
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
                return;

            Exception? unmountEx = null;
            try
            {
                using var cts = new CancellationTokenSource(s_defaultDisposeTimeout);
                await UnmountAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Preserve the original cancel cause to aid postmortem diagnostics
                throw new StorageDisposeTimeoutException(s_defaultDisposeTimeout, ex);
            }
            catch (Exception ex)
            {
                unmountEx = ex;
            }

            try
            {
                _evictionManager.UnregisterCache(_frameLockCache);
                if (_ownsEvictionManager)
                {
                    _evictionManager.Dispose();
                }
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
                TryDisposeLifecycleGateOnce();
            }

            if (unmountEx is not null)
                ExceptionDispatchInfo.Capture(unmountEx).Throw();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnterOperation(out bool gaugeAdded)
        {
            gaugeAdded = false;
            lock (_stateLock)
            {
                if (!_mounted)
                    throw new DeviceNotMountedException();
                _inFlightCount++;
            }
            try
            {
                _inFlightGauge.Add(1);
                gaugeAdded = true;
            }
            catch
            {
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExitOperation(bool gaugeAdded)
        {
            TaskCompletionSource<object>? toDrain = null;
            lock (_stateLock)
            {
                if (_inFlightCount > 0) _inFlightCount--;
                if (_inFlightCount == 0 && !_mounted)
                    toDrain = _drainTcs;
            }
            toDrain?.TrySetResult(null);
            if (gaugeAdded)
            {
                try { _inFlightGauge.Add(-1); } catch { }
            }
        }


        //--- FRAME-GRANULAR I/O (IStorageDevice.ReadFrameAsync / WriteFrameAsync) ---

        public async ValueTask<int> ReadFrameAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (frameId < 0) throw new InvalidOffsetException(frameId);

            if (buffer.Length != Geometry.LogicalBlockSize)
                throw new InvalidLengthException(buffer.Length);

            bool opGaugeAdded = false;
            EnterOperation(out opGaugeAdded);
            try
            {
                // Lock domain is physical frame ids. Logical id N maps to physical id N+1 (skipping metadata at 0).
                long physicalFrameId;
                try
                {
                    physicalFrameId = checked(frameId + 1);
                }
                catch (OverflowException)
                {
                    throw new InvalidOffsetException(frameId);
                }

                int attempt = 0;
                int maxAttempts = MaxLockAcquireAttempts;
                if (maxAttempts < 1) maxAttempts = 1;

                while (true)
                {
                    // Be responsive to cancellation between retries.
                    cancellationToken.ThrowIfCancellationRequested();

                    // 1) Grab or create the per-frame lock (physical)
                    var fl = await _frameLockCache.GetOrAddAsync(physicalFrameId).ConfigureAwait(false);

                    // 2) Compose effective cancellation without per-op allocation unless needed
                    CancellationTokenSource? linkedCts = null;
                    var effectiveToken = GetEffectiveToken(cancellationToken, out linkedCts);
                    // Fail fast on cancellation before any lock work
                    effectiveToken.ThrowIfCancellationRequested();

                    var lockSw = Stopwatch.StartNew();
                    bool acquired = false;
                    bool markedAcquired = false;

                    try
                    {
                        // 3) Wait safely (tracks waiters, guards eviction); retry if evicted before wait completes
                        try
                        {
                            await fl.WaitAsyncSafely(effectiveToken).ConfigureAwait(false);
                        }
                        catch (FrameLockDisposedException)
                        {
                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            TryEmitTrace("lock_acquire", "warn", "retry_on_framelock_disposed", "exid_2012");
                            // Lock was closed before we could acquire; retry with a fresh cache fetch
                            if (++attempt >= maxAttempts)
                                throw new FrameLockDisposedException("Evicted during acquisition.");
                            continue;
                        }
                        acquired = true;
                        // We now definitively hold the lock. Ensure balanced MarkReleased in finally unless we
                        // explicitly neutralize it in a branch that performs its own balanced release.
                        markedAcquired = true;

                        // If the lock was evicted/closed after we fetched it, retry deterministically
                        if (fl.IsClosed)
                        {
                            // We hold the semaphore and have been marked as holder; undo both.
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            finally
                            {
                                if (semReleased)
                                    fl.MarkReleased();
                            }
                            acquired = false;
                            markedAcquired = false;
                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            TryEmitTrace("lock_acquire", "warn", "retry_on_framelock_closed", "exid_2012");

                            if (++attempt >= maxAttempts)
                                throw new FrameLockDisposedException("Evicted during acquisition.");
                            continue; // retry with a fresh cache fetch
                        }
                        else
                        {
                            // Small jittered backoff to avoid hot-loop pressure when eviction churns.
                            // Only applied on an actual retry path; skip for normal success.
                            if (attempt > 0)
                            {
                                // Honor per-device overrides for retry backoff; use effectiveToken (includes lifecycle).
                                double spanMs = (MaxRetryBackoff - MinRetryBackoff).TotalMilliseconds;
                                int backoffMs = (int)(MinRetryBackoff.TotalMilliseconds + (spanMs > 0 ? Random.Shared.NextDouble() * spanMs : 0.0));
                                if (backoffMs > 0) await Task.Delay(backoffMs, effectiveToken).ConfigureAwait(false);
                            }
                        }

                        lockSw.Stop();
                        try
                        {
                            var waitedMs = lockSw.Elapsed.TotalMilliseconds;
                            StorageMetrics.RecordLockWaitMs(_metrics, waitedMs);
                            if (waitedMs > 0.0)
                                StorageMetrics.IncrementContention(_metrics);
                        }
                        catch { }

                        // 4) Already marked acquired; continue to I/O

                        // 5) Do the real I/O
                        var ioSw = Stopwatch.StartNew();
                        // Already using physical lock; perform I/O at the matching physical frame id
                        await ReadFrameInternalAsync(physicalFrameId, buffer, effectiveToken).ConfigureAwait(false);
                        ioSw.Stop();
                        try { StorageMetrics.OnReadCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, buffer.Length); } catch { }
                        return buffer.Length;
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        if (acquired)
                        {
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            finally
                            {
                                if (markedAcquired && semReleased)
                                    fl.MarkReleased();
                            }
                        }
                    }
                }
            }
            finally
            {
                ExitOperation(opGaugeAdded);
            }
        }

        public async ValueTask WriteFrameAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (frameId < 0) throw new InvalidOffsetException(frameId);
            if (data.Length != Geometry.LogicalBlockSize)
                throw new InvalidLengthException(data.Length);

            // Fail fast on not-mounted devices before additional work.
            bool opGaugeAdded = false;
            EnterOperation(out opGaugeAdded);
            try
            {
                // Lock domain is physical frame ids. Logical id N maps to physical id N+1 (skipping metadata at 0).
                long physicalFrameId;
                try
                {
                    physicalFrameId = checked(frameId + 1);
                }
                catch (OverflowException)
                {
                    throw new InvalidOffsetException(frameId);
                }

                int attempt = 0;
                int maxAttempts = MaxLockAcquireAttempts;
                if (maxAttempts < 1) maxAttempts = 1;
                while (true)
                {
                    // Be responsive to cancellation between retries.
                    cancellationToken.ThrowIfCancellationRequested();
                    var fl = await _frameLockCache.GetOrAddAsync(physicalFrameId).ConfigureAwait(false);
                    CancellationTokenSource? linkedCts = null;
                    var effectiveToken = GetEffectiveToken(cancellationToken, out linkedCts);
                    // Fail fast on cancellation before any lock work
                    effectiveToken.ThrowIfCancellationRequested();

                    var lockSw = Stopwatch.StartNew();
                    bool acquired = false;
                    bool markedAcquired = false;

                    try
                    {
                        try
                        {
                            await fl.WaitAsyncSafely(effectiveToken).ConfigureAwait(false);
                        }
                        catch (FrameLockDisposedException)
                        {
                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            TryEmitTrace("lock_acquire", "warn", "retry_on_framelock_disposed", "exid_2012");

                            if (++attempt >= maxAttempts)
                                throw new FrameLockDisposedException("Evicted during acquisition.");
                            continue;
                        }
                        acquired = true;
                        // Ensure holder balance unless neutralized by a branch with explicit balanced release.
                        markedAcquired = true;

                        if (fl.IsClosed)
                        {
                            // We hold the semaphore and have been marked as holder; undo both.
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            finally
                            {
                                if (semReleased)
                                    fl.MarkReleased();
                            }
                            acquired = false;
                            markedAcquired = false;

                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            TryEmitTrace("lock_acquire", "warn", "retry_on_framelock_closed", "exid_2012");
                            if (++attempt >= maxAttempts)
                                throw new FrameLockDisposedException("Evicted during acquisition.");
                            continue;
                        }
                        else
                        {
                            if (attempt > 0)
                            {
                                // Use per-device overrides and effectiveToken for consistency and lifecycle responsiveness.
                                double spanMs = (MaxRetryBackoff - MinRetryBackoff).TotalMilliseconds;
                                int backoffMs = (int)(MinRetryBackoff.TotalMilliseconds + (spanMs > 0 ? Random.Shared.NextDouble() * spanMs : 0.0));
                                if (backoffMs > 0) await Task.Delay(backoffMs, effectiveToken).ConfigureAwait(false);
                            }
                        }


                        lockSw.Stop();
                        try
                        {
                            var waitedMs = lockSw.Elapsed.TotalMilliseconds;
                            StorageMetrics.RecordLockWaitMs(_metrics, waitedMs);
                            if (waitedMs > 0.0)
                                StorageMetrics.IncrementContention(_metrics);
                        }
                        catch { }

                        // Already marked acquired; continue to I/O

                        var ioSw = Stopwatch.StartNew();
                        // Already using physical lock; perform I/O at the matching physical frame id
                        await WriteFrameInternalAsync(physicalFrameId, data, effectiveToken).ConfigureAwait(false);
                        ioSw.Stop();
                        try { StorageMetrics.OnWriteCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, data.Length); } catch { }
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        if (acquired)
                        {
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            finally
                            {
                                if (markedAcquired && semReleased)
                                    fl.MarkReleased();
                            }
                        }
                    }
                    break;
                }
            }
            finally
            {
                ExitOperation(opGaugeAdded);
            }
        }

        //--- OFFSET/LENGTH I/O (IStorageDevice.ReadAsync / WriteAsync) ---

        public async ValueTask<int> ReadAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (offset < 0) throw new InvalidOffsetException(offset);
            if (buffer.Length == 0) return 0;
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (buffer.Length % b) != 0)
                    throw new AlignmentRequiredException(offset, buffer.Length, b);
            }

            CancellationTokenSource? linkedCts = null;
            var effectiveToken = GetEffectiveToken(cancellationToken, out linkedCts);
            bool opGaugeAdded = false;
            EnterOperation(out opGaugeAdded);
            try
            {
                int totalRead = 0;
                int frameSize = Geometry.LogicalBlockSize;
                int maxBytesPerBatch = GetMaxChunkBytes(frameSize);
                while (totalRead < buffer.Length)
                {
                    int remaining = buffer.Length - totalRead;
                    int batchLen = remaining < maxBytesPerBatch ? remaining : maxBytesPerBatch;
                    if (Geometry.RequiresAlignedIo)
                    {
                        // For aligned devices, keep batch multiples of frameSize.
                        int rem = batchLen % frameSize;
                        if (rem != 0) batchLen -= rem;
                        if (batchLen <= 0) batchLen = frameSize;
                    }

                    long chunkOffset = offset + totalRead;
                    var chunkBuffer = buffer.Slice(totalRead, batchLen);

                    // Fail fast on cancellation before acquiring the batch of locks
                    effectiveToken.ThrowIfCancellationRequested();
                    // Acquire locks by physical frame id (skip metadata frame at physical 0).
                    long physicalChunkOffset;
                    try
                    {
                        physicalChunkOffset = checked(chunkOffset + frameSize);
                    }
                    catch (OverflowException)
                    {
                        throw new InvalidOffsetException(chunkOffset);
                    }
                    var frameIds = GetFrameRange(physicalChunkOffset, batchLen, frameSize);
                    var locks = await AcquireFrameLocksAsync(frameIds, effectiveToken).ConfigureAwait(false);
                    try
                    {
                        int n = await ReadUnlockedAsync(chunkOffset, chunkBuffer, effectiveToken).ConfigureAwait(false);
                        totalRead += n;
                    }
                    finally
                    {
                        ReleaseFrameLocks(locks);
                    }
                }
                return totalRead;
            }
            finally
            {
                linkedCts?.Dispose();
                ExitOperation(opGaugeAdded);
            }

        }

        public async ValueTask WriteAsync(
            long offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (offset < 0) throw new InvalidOffsetException(offset);
            if (data.Length == 0) return;
            if (Geometry.RequiresAlignedIo)
            {
                int b = Geometry.LogicalBlockSize;
                if ((offset % b) != 0 || (data.Length % b) != 0)
                    throw new AlignmentRequiredException(offset, data.Length, b);
            }

            CancellationTokenSource? linkedCts = null;
            var effectiveToken = GetEffectiveToken(cancellationToken, out linkedCts);
            bool opGaugeAdded = false;
            EnterOperation(out opGaugeAdded);
            try
            {
                int totalWritten = 0;
                int frameSize = Geometry.LogicalBlockSize;
                int maxBytesPerBatch = GetMaxChunkBytes(frameSize);
                while (totalWritten < data.Length)
                {
                    int remaining = data.Length - totalWritten;
                    int batchLen = remaining < maxBytesPerBatch ? remaining : maxBytesPerBatch;
                    if (Geometry.RequiresAlignedIo)
                    {
                        // For aligned devices, keep batch multiples of frameSize.
                        int rem = batchLen % frameSize;
                        if (rem != 0) batchLen -= rem;
                        if (batchLen <= 0) batchLen = frameSize;
                    }

                    long chunkOffset = offset + totalWritten;
                    var chunkData = data.Slice(totalWritten, batchLen);

                    // Fail fast on cancellation before acquiring the batch of locks
                    effectiveToken.ThrowIfCancellationRequested();
                    // Acquire locks by physical frame id (skip metadata frame at physical 0).
                    long physicalChunkOffset;
                    try
                    {
                        physicalChunkOffset = checked(chunkOffset + frameSize);
                    }
                    catch (OverflowException)
                    {
                        throw new InvalidOffsetException(chunkOffset);
                    }
                    var frameIds = GetFrameRange(physicalChunkOffset, batchLen, frameSize);
                    var locks = await AcquireFrameLocksAsync(frameIds, effectiveToken).ConfigureAwait(false);
                    try
                    {
                        await WriteUnlockedAsync(chunkOffset, chunkData, effectiveToken).ConfigureAwait(false);
                        totalWritten += batchLen;
                    }
                    finally
                    {
                        ReleaseFrameLocks(locks);
                    }
                }
            }
            finally
            {
                linkedCts?.Dispose();
                ExitOperation(opGaugeAdded);
            }

        }

        /// <summary>
        /// Exposes FlushAsync to the interface; delegates to FlushAsyncInternal and records latency.
        /// </summary>
        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            EnsureMounted();
            CancellationTokenSource? linkedCts = null;
            var effectiveToken = GetEffectiveToken(cancellationToken, out linkedCts);
            var sw = Stopwatch.StartNew();
            try
            {
                await FlushAsyncInternal(effectiveToken).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                try { StorageMetrics.RecordFlushLatency(_metrics, sw.Elapsed.TotalMilliseconds); } catch { }
                linkedCts?.Dispose();
            }
        }

        //--- Private helpers for offset/length logic ---

        private async ValueTask<int> ReadUnlockedAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var frameSize = Geometry.LogicalBlockSize;
            var inner = (int)(offset % frameSize);

            // Logical offset starts after metadata frame: shift by one frame
            long physicalOffset;
            try
            {
                physicalOffset = checked(offset + frameSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOffsetException(offset);
            }

            // fast-path single full frame
            if (inner == 0 && buffer.Length == frameSize)
            {
                var ioSw = Stopwatch.StartNew();
                await ReadFrameInternalAsync(physicalOffset / frameSize, buffer, cancellationToken).ConfigureAwait(false);
                ioSw.Stop();
                try { StorageMetrics.OnReadCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, buffer.Length); } catch { }
                return frameSize;
            }

            int total = 0;
            long current = physicalOffset;
            int remaining = buffer.Length;
            byte[] temp = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameId = current / frameSize;
                    var fo = (int)(current % frameSize);
                    var take = Math.Min(frameSize - fo, remaining);

                    var ioSw = Stopwatch.StartNew();
                    await ReadFrameInternalAsync(frameId, temp, cancellationToken).ConfigureAwait(false);
                    ioSw.Stop();
                    try { StorageMetrics.OnReadCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, frameSize); } catch { }

                    new Span<byte>(temp, fo, take)
                        .CopyTo(buffer.Span.Slice(total, take));

                    total += take;
                    current += take;
                    remaining -= take;
                }
                return total;
            }
            finally
            {
                // Clear pooled buffers before returning to reduce data remanence risk.
                ArrayPool<byte>.Shared.Return(temp, clearArray: true);
            }

        }

        private async ValueTask WriteUnlockedAsync(
            long offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            var frameSize = Geometry.LogicalBlockSize;
            var inner = (int)(offset % frameSize);
            long physicalOffset;
            try
            {
                physicalOffset = checked(offset + frameSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOffsetException(offset);
            }

            // fast-path single full frame
            if (inner == 0 && data.Length == frameSize)
            {
                var ioSw = Stopwatch.StartNew();
                await WriteFrameInternalAsync(physicalOffset / frameSize, data, cancellationToken).ConfigureAwait(false);
                ioSw.Stop();
                try { StorageMetrics.OnWriteCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, data.Length); } catch { }
                return;
            }

            long current = physicalOffset;
            int remaining = data.Length;
            byte[] temp = ArrayPool<byte>.Shared.Rent(frameSize);
            int srcIdx = 0;
            try
            {
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameId = current / frameSize;
                    var fo = (int)(current % frameSize);
                    var put = Math.Min(frameSize - fo, remaining);

                    var ioSw = Stopwatch.StartNew();
                    bool zeroFilled = false;
                    try
                    {
                        await ReadFrameInternalAsync(frameId, temp, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DeviceReadOutOfRangeException)
                    {
                        // Treat missing frame as zero-filled for RMW
                        new Span<byte>(temp).Clear();
                        zeroFilled = true;
                    }
                    ioSw.Stop();
                    if (!zeroFilled)
                    {
                        try { StorageMetrics.OnReadCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, frameSize); } catch { }
                    }

                    data.Slice(srcIdx, put).Span.CopyTo(temp.AsSpan(fo, put));

                    ioSw.Restart();
                    await WriteFrameInternalAsync(frameId, temp, cancellationToken).ConfigureAwait(false);
                    ioSw.Stop();
                    try { StorageMetrics.OnWriteCompleted(_metrics, ioSw.Elapsed.TotalMilliseconds, frameSize); } catch { }

                    current += put;
                    srcIdx += put;
                    remaining -= put;
                }
            }
            finally
            {
                // Clear pooled buffers before returning to reduce data remanence risk.
                ArrayPool<byte>.Shared.Return(temp, clearArray: true);
            }
        }

        private static List<long> GetFrameRange(
            long offset,
            int length,
            int frameSize)
        {
            if (length <= 0)
            {
                return new List<long>(0);
            }
            long first = offset / frameSize;
            long last;
            try
            {
                // checked to catch extreme sums
                last = checked((offset + (long)length - 1) / frameSize);
            }
            catch (OverflowException)
            {
                // Fall back to maximal range from first onward to avoid wrap; caller will hit OOM before here in practice
                last = long.MaxValue / frameSize;
            }
            long countLong = last - first + 1;
            int count = countLong > int.MaxValue ? int.MaxValue : (int)countLong;
            var list = new List<long>(count);
            for (long i = first; i <= last; i++)
            {
                list.Add(i);
                if (list.Count == count) break;
            }
            return list;
        }

        private async ValueTask<List<FrameLock>> AcquireFrameLocksAsync(
            List<long> frameIds,
            CancellationToken cancellationToken)
        {
            // Pre-size to incoming count to minimize reallocations on large ranges.
            // Note: final unique count may be smaller after in-place deduplication.
            var locks = new List<FrameLock>(frameIds is null ? 0 : frameIds.Count);
            FrameLock? lastWaited = null;

            int attempts = 0;
            int maxAttempts = MaxLockAcquireAttempts;
            if (maxAttempts < 1) maxAttempts = 1;
            while (true)
            {
                // Respect cancellation across retries of the entire range.
                cancellationToken.ThrowIfCancellationRequested();
                locks.Clear();
                bool success = false;
                bool closedDetected = false;
                // Aggregate total wait across this attempt only
                double aggregatedWaitMs = 0.0;
                bool anyWaitOccurred = false;
                try
                {
                    // Sort and deduplicate in place (callers pass a fresh list per batch)
                    if (frameIds.Count > 1) frameIds.Sort();
                    long prev = long.MinValue;
                    for (int i = 0; i < frameIds.Count; i++)
                    {
                        var currentId = frameIds[i];
                        // Skip duplicate frameIds to avoid self-deadlock on the same semaphore
                        if (i > 0 && currentId == prev) continue;
                        prev = currentId;

                        var fl = await _frameLockCache.GetOrAddAsync(currentId).ConfigureAwait(false);
                        lastWaited = fl;
                        try
                        {
                            // Measure wait per lock and aggregate
                            var waitSw = Stopwatch.StartNew();
                            await fl.WaitAsyncSafely(cancellationToken).ConfigureAwait(false);
                            waitSw.Stop();
                            try
                            {
                                var waited = waitSw.Elapsed.TotalMilliseconds;
                                aggregatedWaitMs += waited;
                                if (waited > 0.0) anyWaitOccurred = true;
                            }
                            catch { }
                        }
                        catch (FrameLockDisposedException)
                        {
                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            TryEmitTrace("range_lock_acquire", "warn", "retry_on_framelock_disposed", "exid_2012");
                            // Closed before acquisition; retry whole range deterministically
                            closedDetected = true;
                            lastWaited = null;
                            break;
                        }
                        if (fl.IsClosed)
                        {
                            try { StorageMetrics.IncrementRetry(_metrics); } catch { }
                            closedDetected = true;
                            TryEmitTrace("range_lock_acquire", "warn", "retry_on_framelock_closed", "exid_2012");
                            // We waited and acquired the semaphore for a closed lock;
                            // release it and drop holder before retrying to avoid deadlock.
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            catch
                            {
                                // swallow; best-effort release
                            }
                            finally
                            {
                                if (semReleased)
                                    fl.MarkReleased();
                            }
                            lastWaited = null;

                            break;
                        }
                        // Already marked acquired by WaitAsyncSafely
                        locks.Add(fl);
                        lastWaited = null;
                    }
                    if (!closedDetected)
                    {
                        // Emit a single aggregated wait metric for the whole batch
                        try
                        {
                            StorageMetrics.RecordLockWaitMs(_metrics, aggregatedWaitMs);
                            if (anyWaitOccurred)
                                StorageMetrics.IncrementContention(_metrics);
                        }
                        catch { }
                        success = true;
                        return locks;
                    }
                }
                finally
                {
                    if (!success)
                    {
                        for (int i = 0; i < locks.Count; i++)
                        {
                            var fl = locks[i];
                            bool semReleased = false;
                            try
                            {
                                fl.Sem.Release();
                                semReleased = true;
                            }
                            finally
                            {
                                if (semReleased)
                                    fl.MarkReleased();
                            }
                        }
                        locks.Clear();
                        // If we broke out on a closed lock after waiting, ensure it's not left held.
                        if (lastWaited is not null)
                        {
                            bool semReleased = false;
                            try
                            {
                                lastWaited.Sem.Release();
                                semReleased = true;
                            }
                            catch
                            {
                            }
                            finally
                            {
                                if (semReleased)
                                    lastWaited.MarkReleased();
                            }
                            lastWaited = null;
                        }
                    }
                }
                attempts++;
                if (attempts >= maxAttempts)
                {
                    throw new FrameLockDisposedException("Evicted during range acquisition.");
                }
                else
                {
                    // Small jittered backoff between range retries to reduce contention
                    // during eviction churn. Kept tiny to avoid visible latency.
                    var spanMs = (MaxRetryBackoff - MinRetryBackoff).TotalMilliseconds;
                    int backoffMs = (int)(MinRetryBackoff.TotalMilliseconds + (spanMs > 0 ? Random.Shared.NextDouble() * spanMs : 0.0));
                    if (backoffMs > 0)
                    {
                        await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                // retry
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryDisposeLifecycleGateOnce()
        {
            if (Interlocked.Exchange(ref _lifecycleGateDisposed, 1) == 0)
            {
                try
                {
                    _lifecycleGate.Dispose();
                }
                catch { }
            }
        }

        private static void ReleaseFrameLocks(
            IEnumerable<FrameLock> locks)
        {
            foreach (var fl in locks)
            {
                bool semReleased = false;
                try
                {
                    fl.Sem.Release();
                    semReleased = true;
                }
                finally
                {
                    if (semReleased)
                        fl.MarkReleased();
                }
            }
        }

        private void EnsureMounted()
        {
            // Prefer disposed signal over "not mounted" to avoid misdiagnosis
            if (Volatile.Read(ref _disposedFlag) == 1)
                throw new DeviceDisposedException();

            lock (_stateLock)
            {
                if (!_mounted)
                    throw new DeviceNotMountedException();
            }
        }

        private int GetMaxChunkBytes(int frameSize)
        {
            int framesPerBatch = MaxFramesPerBatch;
            if (framesPerBatch < 1) framesPerBatch = 1;

            // Avoid int overflow when multiplying frameSize * framesPerBatch
            int limit;
            if (Geometry.MaxIoBytes > 0)
            {
                limit = Geometry.MaxIoBytes;
            }
            else
            {
                long candidate = (long)frameSize * (long)framesPerBatch;
                limit = candidate > int.MaxValue ? int.MaxValue : (int)candidate;
            }

            if (limit < frameSize) limit = frameSize;
            // enforce block alignment
            int rem = limit % frameSize;
            if (rem != 0) limit -= rem;
            if (limit <= 0) limit = frameSize;
            return limit;
        }


        private void ValidateGeometryOrThrow()
        {
            var g = Geometry;
            // Logical block size must be positive.
            if (g.LogicalBlockSize <= 0)
                throw new InvalidGeometryException("Geometry.LogicalBlockSize must be > 0.");
            // Logical block size should be a power of two for predictable alignment/IO boundaries.
            // This prevents pathological configurations and aligns with common device constraints.
            if ((g.LogicalBlockSize & (g.LogicalBlockSize - 1)) != 0)
                throw new InvalidGeometryException("Geometry.LogicalBlockSize must be a power of two.");

            // Max I/O bytes cannot be negative.
            if (g.MaxIoBytes < 0)
                throw new InvalidGeometryException("Geometry.MaxIoBytes must be >= 0.");

            // When bounded, max I/O must align to block size to guarantee batching correctness.
            if (g.MaxIoBytes > 0 && (g.MaxIoBytes % g.LogicalBlockSize) != 0)
                throw new InvalidGeometryException("Geometry.MaxIoBytes must be a multiple of Geometry.LogicalBlockSize when > 0.");

            // When aligned I/O is required, enforce that any batching limit (if present) remains aligned.
            // (MaxIoBytes alignment already enforced above; this documents the invariant explicitly.)
            if (g.RequiresAlignedIo)
            {
                // No-op here beyond the MaxIoBytes check; offset/length callers are checked per request.
                // This branch is intentionally left without additional throws to keep the contract crisp.
                _ = g.LogicalBlockSize; // clarity
            }
        }

        // -------- Metadata support (frame 0) --------

        public void SetExpectedManifest(DeviceManifest manifest)
        {
            // Enforce: must be called before MountAsync on this instance.
            EnsureNotDisposed();
            lock (_stateLock)
            {
                if (_mounted)
                    throw new DeviceAlreadyMountedException();
                // Store for mount-time validation/initialization
                _expectedManifest = manifest;
                // Force re-validation of metadata on next mount for this instance.
                _metadataInitialized = false;
            }
        }

        private async Task InitializeOrValidateMetadataAsync(CancellationToken cancellationToken)
        {
            if (_metadataInitialized) return;

            // Read the whole metadata frame (exact LogicalBlockSize)
            int size = Geometry.LogicalBlockSize;
            byte[] buf = ArrayPool<byte>.Shared.Rent(size);
            bool success = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool exists;
                // Attempt to read metadata frame. If out-of-range, treat as empty (fresh format).
                try
                {
                    await ReadFrameInternalAsync(MetadataFrameId, buf.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                    exists = true;
                }
                catch (DeviceReadOutOfRangeException)
                {
                    exists = false;
                }

                if (!exists || !DeviceManifest.TryDeserialize(new ReadOnlySpan<byte>(buf, 0, size), out var onDisk))
                {
                    // Fresh format required. Must have an expected manifest.
                    if (!_expectedManifest.HasValue)
                        throw new InvalidGeometryException("No manifest present and no expected manifest provided.");

                    var expected = _expectedManifest.Value;
                    if (expected.LogicalBlockSize != Geometry.LogicalBlockSize)
                        throw new InvalidGeometryException("Expected manifest LBS differs from device geometry.");

                    // Serialize into a zeroed buffer
                    new Span<byte>(buf, 0, size).Clear();
                    if (!expected.TrySerialize(new Span<byte>(buf, 0, size)))
                        throw new InvalidGeometryException("Failed to serialize manifest into metadata frame.");
                    await WriteFrameInternalAsync(MetadataFrameId, new ReadOnlyMemory<byte>(buf, 0, size), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Validate against expected if provided; otherwise ensure geometry matches.
                    if (_expectedManifest.HasValue)
                    {
                        var expected = _expectedManifest.Value;
                        if (!onDisk.FormatAffectsLayoutEquals(expected))
                            throw new IncompatibleStackException("Format-affecting mini-driver chain mismatch.");
                    }
                    else
                    {
                        if (onDisk.LogicalBlockSize != Geometry.LogicalBlockSize)
                            throw new InvalidGeometryException("On-disk manifest LBS mismatch.");
                    }
                }
                success = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf, clearArray: true);
                if (success)
                {
                    // Only mark initialized after successful read/validate/serialize flow.
                    _metadataInitialized = true;
                }
            }
        }

        protected async ValueTask<int> ReadMetadataFrameAsync(Memory<byte> destination, CancellationToken token = default)
        {
            EnsureMounted();
            // Enforce exact metadata frame size reads for contract clarity.
            if (destination.Length != Geometry.LogicalBlockSize)
                throw new InvalidLengthException(destination.Length);
            await ReadFrameInternalAsync(MetadataFrameId, destination, token).ConfigureAwait(false);
            return destination.Length;
        }

        /// <summary>
        /// Write raw metadata frame (frame 0) from source.
        /// </summary>
        protected ValueTask WriteMetadataFrameAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            EnsureMounted();
            // Enforce exact metadata frame size writes for contract clarity.
            if (source.Length != Geometry.LogicalBlockSize)
                throw new InvalidLengthException(source.Length);
            return WriteFrameInternalAsync(MetadataFrameId, source, token).AsValueTask();
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryEmitTrace(string operation, string status, string message, string fieldValue)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            try
            {
                // Use only allowed tag keys (Field) to avoid schema drift
                var tags = new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                {
                    { TagKeys.Field, fieldValue }
                };
                DiagnosticsCoreBootstrap.Instance.Traces.Emit(
                    component: _componentName,
                    operation: operation,
                    status: status,
                    tags: tags,
                    message: message);
            }
            catch
            {
                // swallow
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
