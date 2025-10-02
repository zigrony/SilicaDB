// Path: Silica.Storage.Allocation/IStorageAllocator.cs
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Common.Primitives;

namespace Silica.Storage.Allocation
{
    /// <summary>
    /// Base contract for allocation services. Higher layers depend only on this.
    /// </summary>
    public interface IStorageAllocator
    {
        /// <summary>
        /// Allocate a single page for general use (e.g., a data page).
        /// Strategy decides whether this comes from an existing extent or triggers a new extent.
        /// </summary>
        Task<PageId> AllocatePageAsync(CancellationToken ct = default);

        /// <summary>
        /// Allocate a full extent (strategy-defined size) and return the start page.
        /// </summary>
        Task<PageId> AllocateExtentAsync(CancellationToken ct = default);

        /// <summary>
        /// Free a previously allocated page (strategy decides reclamation semantics).
        /// </summary>
        Task FreePageAsync(PageId pageId, CancellationToken ct = default);
    }
}
