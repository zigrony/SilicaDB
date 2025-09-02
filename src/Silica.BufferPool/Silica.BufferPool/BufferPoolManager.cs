// Filename: BufferPoolManager.cs

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Durability;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Extensions.BufferPool;

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
                componentName: poolName,
                residentPagesProvider: () => _frames.Count
                // capacityPagesProvider, dirtyPagesProvider, pinnedPagesProvider, utilizationProvider, inFlightProvider can be wired later
            );

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



    }
}
