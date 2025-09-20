// Filename: Silica.BufferPool\Interfaces\IBufferPoolManager.cs

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.BufferPool
{
    /// <summary>
    /// Contract for buffer pool operations: page leasing, allocation, flushing, and eviction.
    /// Implemented by <see cref="BufferPoolManager"/>.
    /// </summary>
    public interface IBufferPoolManager : IAsyncDisposable
    {
        /// <summary>
        /// Acquire a read lease on the specified page.
        /// Multiple readers may hold leases concurrently.
        /// </summary>
        /// <param name="pageId">The page identifier (file + index).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<PageReadLease> AcquireReadAsync(PageId pageId, CancellationToken ct = default);

        /// <summary>
        /// Acquire a write lease on a byte range within the specified page.
        /// Writers block readers, and overlapping writers block each other.
        /// </summary>
        /// <param name="pageId">The page identifier (file + index).</param>
        /// <param name="offset">Byte offset within the page.</param>
        /// <param name="length">Number of bytes to write.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<PageWriteLease> AcquireWriteAsync(PageId pageId, int offset, int length, CancellationToken ct = default);

        /// <summary>
        /// Flush the specified page to the backing device if it is dirty.
        /// </summary>
        Task FlushPageAsync(PageId pageId, CancellationToken ct = default);

        /// <summary>
        /// Flush all dirty pages in the pool to the backing device.
        /// </summary>
        Task FlushAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Attempt to evict the specified page from the pool.
        /// Only succeeds if the page is unpinned and not in use.
        /// </summary>
        Task<bool> TryEvictAsync(PageId pageId, CancellationToken ct = default);

        /// <summary>
        /// Attempt to evict the specified page from the pool, with a reason tag for metrics.
        /// </summary>
        Task<bool> TryEvictAsync(PageId pageId, string reason, CancellationToken ct = default);

        /// <summary>
        /// Stop the buffer pool: block new leases, wait for active leases to drain,
        /// flush dirty pages, evict all pages, and drain I/O.
        /// </summary>
        Task StopAsync(CancellationToken ct = default);

        /// <summary>
        /// Snapshot the current dirty page set for DPT maintenance, including pageLSN.
        /// Best-effort: the set can change immediately after snapshot.
        /// </summary>
        IReadOnlyList<DirtyPageInfo> SnapshotDirtyPages();

        /// <summary>
        /// Try to get the current pageLSN for a resident page.
        /// </summary>
        bool TryGetPageLsn(PageId pageId, out long pageLsn);
    }
}
