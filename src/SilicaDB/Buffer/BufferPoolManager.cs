// File: BufferPoolManager.cs
// Namespace: SilicaDB.BufferPool

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.BufferPool;
using SilicaDB.Devices.Interfaces;
using SilicaDB.Durability;
using SilicaDB.Evictions.Interfaces;
using SilicaDB.Storage;

namespace SilicaDB.BufferPool
{
    public class BufferPoolManager : IBufferPoolManager
    {
        // -- metrics (example using System.Diagnostics.Metrics)
        private static readonly Meter s_meter = new Meter("SilicaDB.BufferPool", "1.0");
        private static readonly Counter<long> s_pinRequests = s_meter.CreateCounter<long>("bufferpool.pin.requests");
        private static readonly UpDownCounter<long> s_currentPins = s_meter.CreateUpDownCounter<long>("bufferpool.current.pins");
        private static readonly Counter<long> s_flushes = s_meter.CreateCounter<long>("bufferpool.flush.count");
        private static readonly Histogram<double> s_flushLatency = s_meter.CreateHistogram<double>("bufferpool.flush.latency", "ms");

        private readonly IStorageDevice _storage;
        private readonly IAsyncEvictionCache<PageId, FrameSlot> _cache;
        private readonly IWalManager _wal;
        private readonly SemaphoreSlim _sync = new SemaphoreSlim(1, 1);

        // track every loaded frame so we can unpin and flush
        private readonly ConcurrentDictionary<PageId, FrameSlot> _frames = new();

        private bool _disposed;

        public BufferPoolManager(
            IStorageDevice storageDevice,
            IAsyncEvictionCache<PageId, FrameSlot> evictionCache,
            IWalManager walManager)
        {
            _storage = storageDevice ?? throw new ArgumentNullException(nameof(storageDevice));
            _cache = evictionCache ?? throw new ArgumentNullException(nameof(evictionCache));
            _wal = walManager ?? throw new ArgumentNullException(nameof(walManager));
        }

        public async Task<Page> PinPageAsync(PageId pageId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            s_pinRequests.Add(1);
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // load-or-return
                var slot = await _cache.GetOrAddAsync(pageId).ConfigureAwait(false);

                // remember it so Unpin can find it
                _frames.TryAdd(pageId, slot);

                slot.Pin();
                s_currentPins.Add(1);
                return slot.Page;
            }
            finally
            {
                _sync.Release();
            }
        }

        public void UnpinPage(PageId pageId, bool isDirty = false)
        {
            ThrowIfDisposed();
            _sync.Wait();
            try
            {
                if (!_frames.TryGetValue(pageId, out var slot))
                    throw new InvalidOperationException($"Page {pageId} was not pinned.");

                slot.Unpin(isDirty);
                s_currentPins.Add(-1);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task FlushPageAsync(PageId pageId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_frames.TryGetValue(pageId, out var slot) || !slot.IsDirty)
                    return;

                var sw = Stopwatch.StartNew();

                // 1) WAL flush
                await _wal.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 2) Write raw bytes
                await _storage.WriteFrameAsync(
                    pageId.Value,        // long
                    slot.Page.Data,      // byte[]
                    cancellationToken)
                    .ConfigureAwait(false);

                slot.MarkClean();
                sw.Stop();

                s_flushes.Add(1);
                s_flushLatency.Record(sw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task FlushAllAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dirtyPages = _frames
                    .Where(kv => kv.Value.IsDirty)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var pid in dirtyPages)
                    await FlushPageAsync(pid, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            // flush everything
            await FlushAllAsync().ConfigureAwait(false);
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferPoolManager));
        }


    }
}
