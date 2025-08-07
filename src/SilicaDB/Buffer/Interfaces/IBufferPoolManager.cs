using System;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices.Interfaces;
using SilicaDB.Evictions.Interfaces;
using SilicaDB.Durability;
using SilicaDB.Common;
using SilicaDB.Devices;
using SilicaDB.Storage;

namespace SilicaDB.BufferPool
{
    /// <summary>
    /// Manages a fixed pool of in-memory pages, coordinating fetch, eviction, and persistence.
    /// </summary>
    public interface IBufferPoolManager : IAsyncDisposable
    {
        /// <summary>
        /// Pins the requested page in memory, loading it from storage if needed.
        /// Increments that page’s pin count so it won’t be evicted until unpinned.
        /// </summary>
        Task<Page> PinPageAsync(PageId pageId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Decrements the pin count on the specified page.
        /// If isDirty is true, marks the page as dirty so it will be flushed on eviction or DisposeAsync.
        /// </summary>
        void UnpinPage(PageId pageId, bool isDirty = false);

        /// <summary>
        /// Forces a write of a single dirty page through the WAL and down to the storage device.
        /// </summary>
        Task FlushPageAsync(PageId pageId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Flushes all dirty pages currently resident in the buffer pool.
        /// </summary>
        Task FlushAllAsync(CancellationToken cancellationToken = default);
    }
}
