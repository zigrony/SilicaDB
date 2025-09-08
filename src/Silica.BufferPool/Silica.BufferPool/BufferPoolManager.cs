// Filename: BufferPoolManager.cs

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silica.Durability;
using Silica.DiagnosticsCore.Metrics;
using Silica.BufferPool.Metrics;
using Silica.Storage.Metrics;

namespace Silica.BufferPool
{
    // ---------- Contracts ----------

    /// <summary>
    /// Uniquely identifies a page by file and index.
    /// </summary>
    public readonly record struct PageId(long FileId, long PageIndex);

    /// <summary>
    /// Abstraction of a paged storage device.
    /// </summary>
    public interface IPageDevice
    {
        int PageSize { get; }

        Task ReadPageAsync(PageId pageId, Memory<byte> destination, CancellationToken ct);
        Task WritePageAsync(PageId pageId, ReadOnlyMemory<byte> source, CancellationToken ct);
    }

    // ---------- BufferPoolManager ----------

    /// <summary>
    /// Manages an in-memory buffer of pages, with per-page concurrency control,
    /// WAL writes, and eviction support. Emits metrics via IMetricsManager.
    /// </summary>
    public sealed class BufferPoolManager : IAsyncDisposable, IBufferPoolManager
    {
        // Current eviction selection policy: FIFO via _loadOrder queue.
        // Tag into metrics for cross-subsystem policy heatmaps.
        private const string EvictionPolicyTag = "fifo";

        ~BufferPoolManager()
        {
            try
            {
                // Ensure we run once, never block, never throw.
                if (Interlocked.Exchange(ref _finalizerRan, 1) != 0)
                    return;

                Volatile.Write(ref _state, 2);
                Volatile.Write(ref _disposed, true);

                // Optional: flag the missed Dispose (wrap to keep finalizer safe).
                try { BufferPoolMetrics.RecordFinalizedWithoutDispose(_metrics, _poolName); } catch { }

                // Take snapshots needed for cleanup without capturing "this".
                var state = new FinalizerCleanupState(_frames, _metrics, _poolName);

                // Offload best-effort buffer reclamation to the ThreadPool.
                // Do not touch latches, gates, IO, WAL, or locks here.
                ThreadPool.UnsafeQueueUserWorkItem(static s =>
                {
                    var st = (FinalizerCleanupState)s!;
                    try
                    {
                        int reclaimed = 0;
                        foreach (var frame in st.Frames.Values)
                        {
                            try
                            {
                                // Return pooled arrays; non-blocking and idempotent.
                                frame.ReturnBuffer();
                                reclaimed++;
                            }
                            catch { /* best-effort */ }
                        }

                        // Clear dictionary references so frames can be collected.
                        try { st.Frames.Clear(); } catch { }

                        // Optional: metric for reclaimed frames. Safe but still wrapped.
                        try { BufferPoolMetrics.RecordFinalizerReclaimedFrames(st.Metrics, reclaimed, st.PoolName); } catch { }
                    }
                    catch { /* never let this escape */ }
                }, state);
            }
            catch { /* never throw from finalizer */ }
        }


        // Optional hard limit on resident pages (0 = unlimited)
        private readonly int _capacityPages;
        // FIFO of PageIds in load order, for simple eviction
        private readonly Queue<PageId> _loadOrder = new();
        private readonly object _loadOrderLock = new();

        private readonly IPageDevice _device;
        private readonly IMetricsManager _metrics;
        private readonly ConcurrentDictionary<PageId, Frame> _frames;
        private readonly IWalManager? _wal;
        private readonly string _poolName;
        private long _inFlightOps;
        // Lifecycle: 0=Running, 1=ShuttingDown, 2=Disposed
        private int _state;
        // Tracks whether DisposeAsync has already run (legacy guard)
        private bool _disposed;

        // Global lease tracking (acquisition and drain on shutdown)
        private long _activeLeases;
        private readonly ManualResetEventSlim _leasesDrained = new(initialState: true);

        // In-flight IO tracking as a drainable event
        private readonly ManualResetEventSlim _ioDrained = new(initialState: true);

        /// <summary>
        /// Constructs the manager.
        /// </summary>
        /// <param name="device">Underlying page device.</param>
        /// <param name="metrics">Metrics manager to record events.</param>
        /// <param name="wal">Optional WAL manager for durability.</param>
        /// <param name="poolName">Logical name to tag metrics.</param>
        /// <param name="capacityPages">
        ///   Max number of pages to keep in memory (0 = unlimited).
        /// </param>
        public BufferPoolManager(
            IPageDevice device,
            IMetricsManager metrics,
            IWalManager? wal = null,
            string poolName = "DefaultPool",
            int capacityPages = 0)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            if (device.PageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(device.PageSize), "PageSize must be positive.");
            if (capacityPages < 0)
                throw new ArgumentOutOfRangeException(nameof(capacityPages));
            _capacityPages = capacityPages;
            _wal = wal;
            _frames = new ConcurrentDictionary<PageId, Frame>();

            _poolName = string.IsNullOrWhiteSpace(poolName) ? "DefaultPool" : poolName;

            // Register buffer-pool metrics (hits, misses, flushes, evictions, resident count)
            BufferPoolMetrics.RegisterAll(
                _metrics,
                componentName: _poolName,
                residentPagesProvider: () => _frames.Count,
                capacityPagesProvider: () => _capacityPages,
                dirtyPagesProvider: () => CountDirty(),
                pinnedPagesProvider: () => CountPinned(),
                utilizationProvider: () =>
                {
                    if (_capacityPages <= 0)
                        return 0.0;
                    return (double)_frames.Count / _capacityPages;
                },
                inFlightProvider: new InFlightProvider(() => Interlocked.Read(ref _inFlightOps))
            );
        }

        // inside BufferPoolManager
        private int _finalizerRan; // 0=no, 1=yes

        private sealed class FinalizerCleanupState
        {
            public readonly ConcurrentDictionary<PageId, Frame> Frames;
            public readonly IMetricsManager Metrics;
            public readonly string PoolName;

            public FinalizerCleanupState(
                ConcurrentDictionary<PageId, Frame> frames,
                IMetricsManager metrics,
                string poolName)
            {
                Frames = frames;
                Metrics = metrics;
                PoolName = poolName;
            }
        }

        // New: shared disposal core to make leases idempotent
        internal sealed class LeaseCore
        {
            private int _disposed;
            private readonly BufferPoolManager _owner;
            private readonly Frame _frame;
            private readonly AsyncRangeLatch.Releaser _releaser;

            public LeaseCore(BufferPoolManager owner, Frame frame, AsyncRangeLatch.Releaser releaser)
            {
                _owner = owner;
                _frame = frame;
                _releaser = releaser;
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                try
                {
                    await _releaser.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _frame.Unpin();
                    _owner.OnLeaseReleased();
                }
            }
        }


        /// <summary>
        /// Acquire a read lease on the page. Multiple concurrent readers allowed.
        /// </summary>
        public async Task<PageReadLease> AcquireReadAsync(
            PageId pid,
            CancellationToken ct = default)
        {
            for (; ; )
            {
                ThrowIfNotAccepting();
                var frame = await GetOrCreateAndLoadFrameAsync(pid, ct)
                                .ConfigureAwait(false);

                var t0 = Stopwatch.GetTimestamp();
                AsyncRangeLatch.Releaser releaser;
                try
                {
                    releaser = await frame.Latch.AcquireReadAsync(ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Latch was closed due to eviction; retry with a fresh frame
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Latch canceled due to eviction/dispose (not user cancellation); retry.
                    continue;
                }
                var waitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                BufferPoolMetrics.RecordLatchWaitMs(_metrics, waitMs);
                try
                {
                    await frame.PinAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await releaser.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                OnLeaseAcquired();
                var core = new LeaseCore(this, frame, releaser);
                return new PageReadLease(core, frame.Buffer);
            }

        }

        /// <summary>
        /// Attempts eviction of a page; if it cannot be evicted (e.g. still pinned),
        /// records a blocked eviction metric and re-queues it for later retry.
        /// </summary>
        private async Task EvictOldest(PageId pageId, CancellationToken ct = default)
        {
            try
            {
                var removed = await TryEvictAsync(pageId, BufferPoolMetrics.Fields.Capacity, ct).ConfigureAwait(false);
                if (!removed)
                {
                    // Eviction was blocked (likely pinned). Only re-queue if the frame still exists.
                    // Prevent phantom churn when the page was already evicted by another path.
                    if (_frames.ContainsKey(pageId))
                    {
                        BufferPoolMetrics.RecordEvictionBlocked(_metrics);
                        lock (_loadOrderLock)
                            _loadOrder.Enqueue(pageId);
                    }
                }
            }
            catch (Exception ex)
            {
                // best-effort: record eviction exception via DiagnosticsCore
                try
                {
                    BufferPoolMetrics.RecordEvictionError(_metrics, ex);
                }
                catch
                {
                    // swallow anything further—metrics must never throw
                }
            }
        }

        /// <summary>
        /// Acquire a write lease on a byte range of the page.
        /// Writers block readers, and overlapping writers block each other.
        /// </summary>
        public async Task<PageWriteLease> AcquireWriteAsync(
            PageId pid,
            int offset,
            int length,
            CancellationToken ct = default)
        {
            // Validate bounds before acquiring the latch
            if ((uint)offset > (uint)_device.PageSize)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if ((uint)length == 0 || (long)offset + (long)length > _device.PageSize)
                throw new ArgumentOutOfRangeException(nameof(length));

            for (; ; )
            {
                ThrowIfNotAccepting();
                var frame = await GetOrCreateAndLoadFrameAsync(pid, ct)
                                .ConfigureAwait(false);

                var t0 = Stopwatch.GetTimestamp();
                AsyncRangeLatch.Releaser releaser;
                try
                {
                    releaser = await frame.Latch.AcquireWriteAsync(offset, length, ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Latch was closed due to eviction; retry
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Latch canceled due to eviction/dispose (not user cancellation); retry.
                    continue;
                }

                var waitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                BufferPoolMetrics.RecordLatchWaitMs(
                    _metrics,
                    waitMs,
                    BufferPoolMetrics.Fields.RangeOverlap);
                try
                {
                    await frame.PinAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await releaser.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                frame.MarkDirty();
                OnLeaseAcquired();
                var core = new LeaseCore(this, frame, releaser);
                return new PageWriteLease(core, frame.Buffer);
            }
        }
        /// <summary>
        /// Flushes one page if dirty: write WAL record then page device.
        /// </summary>
        public async Task FlushPageAsync(PageId pageId, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (!_frames.TryGetValue(pageId, out var frame))
                return;
            // If the frame isn't fully loaded yet, skip (cannot be dirty).
            if (!frame.Loaded)
                return;

            // full-page barrier
            var barrier = await frame.Latch.AcquireWriteAsync(0, _device.PageSize, ct)
                .ConfigureAwait(false);

            try
            {
                await FlushFrameIfDirtyAsync(pageId, frame, ct).ConfigureAwait(false);
            }
            finally
            {
                await barrier.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Flushes all dirty pages in the pool.
        /// </summary>
        public async Task FlushAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            foreach (var pid in _frames.Keys)
            {
                ct.ThrowIfCancellationRequested();
                await FlushPageAsync(pid, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Attempts to evict a page. Only succeeds if unpinned and no active R/W.
        /// Dirty pages are flushed first.
        /// </summary>
        public async Task<bool> TryEvictAsync(
            PageId pageId,
            CancellationToken ct = default)
            => await TryEvictAsync(pageId, BufferPoolMetrics.Fields.Idle, ct).ConfigureAwait(false);

        /// <summary>
        /// Attempts to evict a page with an explicit reason for metrics attribution.
        /// Only succeeds if unpinned, fully loaded, and with no active R/W. Dirty pages are flushed first.
        /// </summary>
        public async Task<bool> TryEvictAsync(
            PageId pageId,
            string reason,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (!_frames.TryGetValue(pageId, out var frame))
                return false;

            // Double-check pin count under latch to avoid race
            if (frame.PinCount > 0)
                return false;
            // Do not evict frames still in the loading phase; avoid disposing LoadGate
            // while another thread is awaiting it.
            if (!frame.Loaded)
                return false;

            var barrier = await frame.Latch.AcquireWriteAsync(0, _device.PageSize, ct)
                .ConfigureAwait(false);

            try
            {
                if (frame.PinCount > 0)
                    return false;

                await FlushFrameIfDirtyAsync(pageId, frame, ct).ConfigureAwait(false);
                var removed = _frames.TryRemove(
                    new KeyValuePair<PageId, Frame>(pageId, frame));

                if (removed) 
                {
                    // Dispose sync primitives under the full-page barrier
                    frame.Latch.Dispose();
                    frame.LoadGate.Dispose();
                    BufferPoolMetrics.IncrementEviction(
                        _metrics,
                        reason,
                        EvictionPolicyTag);
                    BufferPoolMetrics.RecordBufferReturned(_metrics);
                    frame.ReturnBuffer();
                }
                return removed;
            }
            finally
            {
                await barrier.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stop the pool: block new leases, wait for active leases, flush, evict, and drain IO.
        /// </summary>
        public async Task StopAsync(CancellationToken ct = default)
        {
            // Transition to ShuttingDown only once.
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;

            // Block until all outstanding leases are released.
            await Task.Run(() => _leasesDrained.Wait(ct), ct).ConfigureAwait(false);

            // Flush all pages best-effort.
            foreach (var pid in _frames.Keys)
            {
                ct.ThrowIfCancellationRequested();
                await FlushPageAsync(pid, ct).ConfigureAwait(false);
            }

            // Evict all frames. Iterate until dictionary is empty or no progress; respect cancellation.
            for (; ; )
            {
                bool progress = false;
                foreach (var pid in _frames.Keys)
                {
                    ct.ThrowIfCancellationRequested();
                    var evicted = await TryEvictAsync(pid, BufferPoolMetrics.Fields.Shutdown, ct).ConfigureAwait(false);
                    if (evicted) progress = true;
                }
                if (!progress)
                    break;
            }

            // Wait for any lingering WAL/IO to complete.
            await Task.Run(() => _ioDrained.Wait(ct), ct).ConfigureAwait(false);

            // Mark disposed state.
            Volatile.Write(ref _state, 2);
            _disposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                // 1) Ensure both state and disposed flag are set
                Volatile.Write(ref _state, 2);
                Volatile.Write(ref _disposed, true);
                // 2) Drain any remaining per-frame primitives
                foreach (var frame in _frames.Values)
                {
                    try
                    {
                        frame.Latch.Dispose();
                        frame.LoadGate.Dispose();
                        frame.ReturnBuffer();
                    }
                    catch { /* best-effort */ }
                }
                // 3) Dispose top-level wait handles
                _leasesDrained.Dispose();
                _ioDrained.Dispose();

                GC.SuppressFinalize(this);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Atomically load a page from device on first access and count hits/misses.
        /// </summary>
        private async Task<Frame> GetOrCreateAndLoadFrameAsync(
            PageId pageId,
            CancellationToken ct)
        {
            var frame = _frames.GetOrAdd(pageId, _ => new Frame());

            if (frame.TryMarkLoadingOrLoaded(out var wasLoaded) && wasLoaded)
            {
                BufferPoolMetrics.IncrementHit(_metrics);
                return frame;
            }

            bool gateTaken = false;
            try
            {
                await frame.LoadGate.WaitAsync(ct).ConfigureAwait(false);
                gateTaken = true;
                if (frame.Loaded)
                {
                    BufferPoolMetrics.IncrementHit(_metrics);
                    return frame;
                }

                frame.InitializeBuffer(_device.PageSize);
                BufferPoolMetrics.IncrementPageFault(_metrics);
                IncrementInFlight();
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    await _device.ReadPageAsync(pageId, frame.Buffer, ct).ConfigureAwait(false);
                }
                finally
                {
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    BufferPoolMetrics.OnReadCompleted(_metrics, ms, _device.PageSize);
                    DecrementInFlight();
                }
                frame.MarkLoaded();

                // Track load order & evict oldest if over capacity
                if (_capacityPages > 0)
                {
                    lock (_loadOrderLock)
                    {
                        _loadOrder.Enqueue(pageId);
                        // Schedule enough evictions to catch up to capacity at this moment.
                        while (_frames.Count > _capacityPages && _loadOrder.Count > 0)
                        {
                            var oldest = _loadOrder.Dequeue();
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await EvictOldest(oldest, CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // ignore
                                }
                            });
                        }
                    }
                }
                BufferPoolMetrics.IncrementMiss(_metrics);
                return frame;
            }
            finally
            {
                if (gateTaken)
                    frame.LoadGate.Release();
            }
        }

        /// <summary>
        /// Format: [8B fileId][8B index][PageSize bytes of data].
        /// </summary>
        private static byte[] EncodeFullPageRecord(
            in PageId pageId,
            ReadOnlySpan<byte> page,
            int pageSize)
        {
            if (page.Length != pageSize)
                throw new ArgumentException(nameof(page));
            // Prevent integer overflow in allocation
            if (pageSize < 0 || pageSize > int.MaxValue - 16)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size too large for WAL payload.");
            var buf = new byte[16 + pageSize];
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), pageId.FileId);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), pageId.PageIndex);
            page.CopyTo(buf.AsSpan(16, pageSize));
            return buf;
        }


        // Unified dirty flush for both explicit flush and eviction paths.
        private async Task FlushFrameIfDirtyAsync(PageId pageId, Frame frame, CancellationToken ct)
        {
            if (!frame.IsDirty) return;

            if (_wal != null)
            {
                var payload = EncodeFullPageRecord(
                    pageId,
                    frame.Buffer.Span,
                    _device.PageSize);

                IncrementInFlight();
                var tWal = Stopwatch.GetTimestamp();
                try
                {
                    await _wal.AppendAsync(new WalRecord(0, payload), ct).ConfigureAwait(false);
                    await _wal.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    var walMs = Stopwatch.GetElapsedTime(tWal).TotalMilliseconds;
                    BufferPoolMetrics.RecordWalAppend(_metrics, walMs, payload.LongLength);
                    DecrementInFlight();
                }
            }

            IncrementInFlight();
            var tIo = Stopwatch.GetTimestamp();
            try
            {
                await _device.WritePageAsync(pageId, frame.Buffer, ct).ConfigureAwait(false);
            }
            finally
            {
                var ioMs = Stopwatch.GetElapsedTime(tIo).TotalMilliseconds;
                BufferPoolMetrics.OnWriteCompleted(_metrics, ioMs, _device.PageSize);
                BufferPoolMetrics.OnFlushCompleted(_metrics, ioMs);
                DecrementInFlight();
            }
            frame.ClearDirty();
        }

        // In-flight accounting with drain signaling.
        private void IncrementInFlight()
        {
            if (Interlocked.Increment(ref _inFlightOps) == 1)
                _ioDrained.Reset();
        }

        private void DecrementInFlight()
        {
            if (Interlocked.Decrement(ref _inFlightOps) == 0)
                _ioDrained.Set();
        }


        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));
        }

        // ---- Gauge helpers ----
        private int CountDirty()
        {
            var c = 0;
            foreach (var kv in _frames)
                if (kv.Value.IsDirty) c++;
            return c;
        }

        private int CountPinned()
        {
            var c = 0;
            foreach (var kv in _frames)
                if (kv.Value.PinCount > 0) c++;
            return c;
        }

        private sealed class InFlightProvider : Silica.BufferPool.Metrics.IInFlightProvider
        {
            private readonly Func<long> _read;
            public InFlightProvider(Func<long> read) => _read = read;
            public long Current => _read();
        }

        // Add near the other private helpers in BufferPoolManager
        private void ThrowIfNotAccepting()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));
            if (_state == 1)
                throw new InvalidOperationException(
                    "BufferPoolManager is shutting down and no longer accepts new leases.");
            if (_state != 0)
                throw new InvalidOperationException(
                    $"BufferPoolManager in invalid state '{_state}'.");
        }

        private void OnLeaseAcquired()
        {
            _leasesDrained.Reset();
            Interlocked.Increment(ref _activeLeases);
        }

        internal void OnLeaseReleased()
        {
            if (Interlocked.Decrement(ref _activeLeases) == 0)
                _leasesDrained.Set();
        }


    }
}
