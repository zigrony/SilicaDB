// Filename: BufferPoolManager.cs

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Durability;
using Silica.Observability.Metrics;
using Silica.Observability.Metrics.Interfaces;

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
    public sealed class BufferPoolManager : IAsyncDisposable
    {
        private readonly IPageDevice _device;
        private readonly IMetricsManager _metrics;
        private readonly ConcurrentDictionary<PageId, Frame> _frames;
        private readonly IWalManager? _wal;
        // Tracks whether DisposeAsync has already run
        private bool _disposed;

        /// <summary>
        /// Constructs the manager.
        /// </summary>
        /// <param name="device">Underlying page device.</param>
        /// <param name="metrics">Metrics manager to record events.</param>
        /// <param name="wal">Optional WAL manager for durability.</param>
        /// <param name="poolName">Logical name to tag metrics.</param>
        public BufferPoolManager(
            IPageDevice device,
            IMetricsManager metrics,
            IWalManager? wal = null,
            string poolName = "DefaultPool")
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _wal = wal;
            _frames = new ConcurrentDictionary<PageId, Frame>();

            // Register buffer-pool metrics (hits, misses, flushes, evictions, resident count)
            BufferPoolMetrics.RegisterAll(
                _metrics,
                poolName,
                () => _frames.Count);
        }

        /// <summary>
        /// Acquire a read lease on the page. Multiple concurrent readers allowed.
        /// </summary>
        public async Task<PageReadLease> AcquireReadAsync(
            PageId pid,
            CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));

            var frame = await GetOrCreateAndLoadFrameAsync(pid, ct)
                            .ConfigureAwait(false);

            // NEW: block eviction first, then pin under the barrier
            var releaser = await frame.Latch.AcquireReadAsync(ct)
                             .ConfigureAwait(false);
            try
            {
                await frame.PinAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // If pin fails, drop the latch immediately
                await releaser.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new PageReadLease(frame, releaser, frame.Buffer);
        }

        /// <summary>
        /// Acquire a write lease on a byte range of the page.
        /// Writers block readers, and overlapping writers block each other.
        /// </summary>
        public async Task<PageWriteLease> AcquireWriteAsync(
            PageId pid,
            int offset,
            int length,
            CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));

            var frame = await GetOrCreateAndLoadFrameAsync(pid, ct)
                            .ConfigureAwait(false);

            // NEW: acquire full-page barrier first, then pin
            var releaser = await frame.Latch.AcquireWriteAsync(offset, length, ct)
                             .ConfigureAwait(false);
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
            return new PageWriteLease(frame, releaser,
                                      frame.Buffer.Slice(offset, length));
        }
        /// <summary>
        /// Flushes one page if dirty: write WAL record then page device.
        /// </summary>
        public async Task FlushPageAsync(PageId pageId, CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));

            if (!_frames.TryGetValue(pageId, out var frame))
                return;

            // full-page barrier
            var barrier = await frame.Latch.AcquireWriteAsync(0, _device.PageSize, ct)
                .ConfigureAwait(false);

            try
            {
                if (!frame.IsDirty) return;

                if (_wal != null)
                {
                    var payload = EncodeFullPageRecord(
                        pageId,
                        frame.Buffer.Span,
                        _device.PageSize);

                    await _wal.AppendAsync(new WalRecord(0, payload), ct)
                        .ConfigureAwait(false);
                    await _wal.FlushAsync(ct).ConfigureAwait(false);
                }

                await _device.WritePageAsync(pageId, frame.Buffer, ct)
                    .ConfigureAwait(false);
                frame.ClearDirty();

                _metrics.Increment(BufferPoolMetrics.Flushes.Name);
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
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));

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
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));

            if (!_frames.TryGetValue(pageId, out var frame))
                return false;
            if (frame.PinCount > 0)
                return false;

            var barrier = await frame.Latch.AcquireWriteAsync(0, _device.PageSize, ct)
                .ConfigureAwait(false);

            try
            {
                if (frame.PinCount > 0)
                    return false;

                if (frame.IsDirty)
                {
                    if (_wal != null)
                    {
                        var payload = EncodeFullPageRecord(
                            pageId,
                            frame.Buffer.Span,
                            _device.PageSize);

                        await _wal.AppendAsync(new WalRecord(0, payload), ct)
                            .ConfigureAwait(false);
                        await _wal.FlushAsync(ct).ConfigureAwait(false);
                    }

                    await _device.WritePageAsync(pageId, frame.Buffer, ct)
                        .ConfigureAwait(false);

                    frame.ClearDirty();
                    _metrics.Increment(BufferPoolMetrics.Flushes.Name);
                }

                var removed = _frames.TryRemove(
                    new KeyValuePair<PageId, Frame>(pageId, frame));

                if (removed) 
                { 
                    _metrics.Increment(BufferPoolMetrics.Evictions.Name);
                    frame.ReturnBuffer();
                }
                return removed;
            }
            finally
            {
                await barrier.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            // best-effort flush
            try 
            { 
                await FlushAllAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var frame in _frames.Values)
                    frame.ReturnBuffer();
                _frames.Clear();
            }
            catch { }
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
                _metrics.Increment(BufferPoolMetrics.Hits.Name);
                return frame;
            }

            // Replace the entire loading logic with this:
            await frame.LoadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (frame.Loaded)
                {
                    _metrics.Increment(BufferPoolMetrics.Hits.Name);
                    return frame;
                }

                frame.InitializeBuffer(_device.PageSize);
                await _device.ReadPageAsync(pageId, frame.Buffer, ct)
                    .ConfigureAwait(false);
                frame.MarkLoaded();

                _metrics.Increment(BufferPoolMetrics.Misses.Name);
                return frame;
            }
            finally
            {
                // Exactly one Release() per WaitAsync()
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

            var buf = new byte[16 + pageSize];
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), pageId.FileId);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), pageId.PageIndex);
            page.CopyTo(buf.AsSpan(16, pageSize));
            return buf;
        }

        // ─────────────────────────────────────────────────────────────
        // Frame: per-page state
        // ─────────────────────────────────────────────────────────────

        internal sealed class Frame
        {
            private int _pinCount;
            private volatile bool _dirty;
            private int _loadState; // 0=none,1=loading-or-loaded,2=loaded

            public readonly SemaphoreSlim LoadGate = new(1, 1);
            private byte[]? _bytes;

            public readonly AsyncRangeLatch Latch = new();

            public Memory<byte> Buffer =>
                (_bytes ?? throw new InvalidOperationException("Buffer uninitialized"))
                .AsMemory();

            public bool Loaded => Volatile.Read(ref _loadState) == 2;

            public bool TryMarkLoadingOrLoaded(out bool alreadyLoaded)
            {
                var s = Volatile.Read(ref _loadState);
                if (s == 2) { alreadyLoaded = true; return true; }
                if (s == 1) { alreadyLoaded = false; return true; }
                alreadyLoaded = false;
                return Interlocked.CompareExchange(ref _loadState, 1, 0) == 0;
            }

            public void InitializeBuffer(int size)
            {
                if (_bytes is null)
                    _bytes = ArrayPool<byte>.Shared.Rent(size);
            }

            internal void ReturnBuffer()
            {
                if (_bytes != null)
                {
                    ArrayPool<byte>.Shared.Return(_bytes, clearArray: true);
                    _bytes = null;
                }
            }

            public void MarkLoaded() => Volatile.Write(ref _loadState, 2);

            public int PinCount => Volatile.Read(ref _pinCount);

            public Task PinAsync(CancellationToken _)
            {
                Interlocked.Increment(ref _pinCount);
                return Task.CompletedTask;
            }

            public void Unpin()
            {
                if (Interlocked.Decrement(ref _pinCount) < 0)
                    throw new InvalidOperationException("Unbalanced pin/unpin.");
            }

            public bool IsDirty => Volatile.Read(ref _dirty);
            public void MarkDirty() => Volatile.Write(ref _dirty, true);
            public void ClearDirty() => Volatile.Write(ref _dirty, false);
        }

        // ─────────────────────────────────────────────────────────────
        // Leases
        // ─────────────────────────────────────────────────────────────

        public readonly struct PageReadLease : IAsyncDisposable
        {
            private readonly Frame _frame;
            private readonly AsyncRangeLatch.Releaser _release;
            public readonly ReadOnlyMemory<byte> Page;

            internal PageReadLease(Frame f, AsyncRangeLatch.Releaser r, Memory<byte> p)
            {
                _frame = f;
                _release = r;
                Page = p;
            }

            public async ValueTask DisposeAsync()
            {
                await _release.DisposeAsync().ConfigureAwait(false);
                _frame.Unpin();
            }
        }

        public readonly struct PageWriteLease : IAsyncDisposable
        {
            private readonly Frame _frame;
            private readonly AsyncRangeLatch.Releaser _release;
            public readonly Memory<byte> Slice;

            internal PageWriteLease(Frame f, AsyncRangeLatch.Releaser r, Memory<byte> s)
            {
                _frame = f;
                _release = r;
                Slice = s;
            }

            public async ValueTask DisposeAsync()
            {
                await _release.DisposeAsync().ConfigureAwait(false);
                _frame.Unpin();
            }
        }
    }

    // ---------- AsyncRangeLatch ----------

    /// <summary>
    /// Coordinated per-page latch:
    /// - multiple readers if no writer
    /// - writers block readers and other overlapping writers
    /// - FIFO queues, writer preference
    /// </summary>
    /// <summary>
    /// Coordinated per-page latch:
    /// - multiple readers if no writer
    /// - writers block readers and other overlapping writers
    /// - FIFO queues, writer preference
    /// </summary>
    internal sealed class AsyncRangeLatch : IDisposable, IAsyncDisposable
    {
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private int _activeReaders;
        private readonly List<RangeSeg> _activeWrites = new();
        private readonly Queue<TaskCompletionSource<Releaser>> _readerQ = new();
        private readonly Queue<WriterWait> _writerQ = new();
        private bool _disposed;

        public async Task<Releaser> AcquireReadAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            TaskCompletionSource<Releaser> tcs;

            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_activeWrites.Count == 0 && _writerQ.Count == 0)
                {
                    _activeReaders++;
                    return new Releaser(this, isWriter: false, seg: default);
                }
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerQ.Enqueue(tcs);
            }
            finally
            {
                _mutex.Release();
            }

            using var reg = ct.CanBeCanceled
                ? ct.Register(static s => ((TaskCompletionSource<Releaser>)s!).TrySetCanceled(), tcs)
                : default;

            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<Releaser> AcquireWriteAsync(
            int offset,
            int length,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            var seg = new RangeSeg(offset, offset + length);

            TaskCompletionSource<Releaser> tcs;
            WriterWait waiter;
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_activeReaders == 0 && !OverlapsAny(_activeWrites, seg))
                {
                    _activeWrites.Add(seg);
                    return new Releaser(this, isWriter: true, seg);
                }
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = new WriterWait(seg, tcs);
                _writerQ.Enqueue(waiter);
                //_writerQ.Enqueue(new WriterWait(seg, tcs));
            }
            finally
            {
                _mutex.Release();
            }

            // Register cancellation on the same TCS
            //using var reg = ct.CanBeCanceled
            //    ? ct.Register(static s => ((WriterWait)s!).TryCancel(), new WriterWait(seg, tcs))
            //    : default;
            using var reg = ct.CanBeCanceled
                  ? ct.Register(static s => ((WriterWait)s!).TryCancel(), waiter)
                  : default;
            return await tcs.Task.ConfigureAwait(false);
        }

        private static bool OverlapsAny(List<RangeSeg> list, RangeSeg seg)
        {
            foreach (var w in list)
                if (w.Overlaps(seg)) return true;
            return false;
        }

        public async ValueTask ReleaseAsync(bool isWriter, RangeSeg seg)
        {
            ThrowIfDisposed();
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (isWriter)
                {
                    _activeWrites.Remove(seg);
                }
                else
                {
                    _activeReaders--;
                    if (_activeReaders < 0)
                        throw new InvalidOperationException("Reader underflow");
                }

                Promote();
            }
            finally
            {
                _mutex.Release();
            }
        }

        private void Promote()
        {
            // Writers first if no readers
            if (_writerQ.Count > 0)
            {
                var snapshot = new List<RangeSeg>(_activeWrites);
                var remaining = new Queue<WriterWait>();
                bool grantedAny = false;

                while (_writerQ.Count > 0)
                {
                    var w = _writerQ.Dequeue();
                    if (w.IsCanceled)
                        continue;

                    if (!OverlapsAny(snapshot, w.Range))
                    {
                        _activeWrites.Add(w.Range);
                        snapshot.Add(w.Range);
                        w.Tcs.TrySetResult(new Releaser(this, isWriter: true, w.Range));
                        grantedAny = true;
                    }
                    else
                    {
                        remaining.Enqueue(w);
                    }
                }
                foreach (var w in remaining)
                    _writerQ.Enqueue(w);

                if (grantedAny)
                    return;   // do not wake readers this cycle
            }

            // Then readers if no writers waiting or active
            if (_activeWrites.Count == 0 && _writerQ.Count == 0 && _readerQ.Count > 0)
            {
                int n = _readerQ.Count;
                _activeReaders += n;
                while (n-- > 0)
                    _readerQ.Dequeue().TrySetResult(new Releaser(this, isWriter: false, default));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncRangeLatch));
        }

        // -------------------------------------------------------------
        // IDisposable / IAsyncDisposable to free the internal semaphore
        // -------------------------------------------------------------
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mutex.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await ValueTask.CompletedTask;
        }

        // ------------------------------------------------------------------
        // Nested helper types
        // ------------------------------------------------------------------

        private sealed class WriterWait
        {
            public RangeSeg Range { get; }
            public TaskCompletionSource<Releaser> Tcs { get; }
            private int _canceled;   // 0 = live, 1 = canceled

            public bool IsCanceled => _canceled == 1;

            public WriterWait(RangeSeg range, TaskCompletionSource<Releaser> tcs)
            {
                Range = range;
                Tcs = tcs;
            }

            public void TryCancel()
            {
                if (Interlocked.CompareExchange(ref _canceled, 1, 0) == 0)
                    Tcs.TrySetCanceled();
            }
        }

        public readonly struct Releaser : IAsyncDisposable
        {
            private readonly AsyncRangeLatch _owner;
            private readonly bool _writer;
            private readonly RangeSeg _seg;

            internal Releaser(AsyncRangeLatch owner, bool isWriter, RangeSeg seg)
            {
                _owner = owner;
                _writer = isWriter;
                _seg = seg;
            }

            public ValueTask DisposeAsync()
                => _owner.ReleaseAsync(_writer, _seg);
        }

        public readonly struct RangeSeg : IEquatable<RangeSeg>
        {
            public int Start { get; }
            public int End { get; }

            public RangeSeg(int start, int end)
            {
                if (start < 0 || end <= start)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Start = start;
                End = end;
            }

            public bool Overlaps(RangeSeg other)
                => Start < other.End && other.Start < End;

            public bool Equals(RangeSeg other)
                => Start == other.Start && End == other.End;

            public override bool Equals(object? obj)
                => obj is RangeSeg r && Equals(r);

            public override int GetHashCode()
                => HashCode.Combine(Start, End);
        }
    }
}
