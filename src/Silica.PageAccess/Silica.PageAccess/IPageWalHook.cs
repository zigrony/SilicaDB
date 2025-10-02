// File: Silica.PageAccess/IPageWalHook.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Common.Primitives;

namespace Silica.PageAccess
{
    /// <summary>
    /// Pluggable WAL hook used by PageAccess to enforce WAL-before-write without
    /// coupling to durability implementation. Transactions layer implements this.
    /// </summary>
    public interface IPageWalHook
    {
        /// <summary>
        /// Called before a page write becomes visible on disk. Must append a log record
        /// that describes the change (schema is owned by the caller), and return the
        /// assigned LSN to stamp into the page header. Implementations should ensure
        /// WAL-before-write (e.g., by flushing or arranging flush ordering).
        /// </summary>
        /// <param name="pageId">Target page.</param>
        /// <param name="offset">Byte offset within the page of the updated region.</param>
        /// <param name="length">Length of the updated region.</param>
        /// <param name="afterImage">
        /// The updated bytes of the region [offset..offset+length). The buffer is owned
        /// by the caller and may be reused; copy if you need to retain it.
        /// </param>
        /// <param name="ct">Cancellation.</param>
        /// <returns>Assigned LSN to stamp into the page header.</returns>
        ValueTask<ulong> OnBeforePageWriteAsync(
            PageId pageId,
            int offset,
            int length,
            ReadOnlyMemory<byte> afterImage,
            CancellationToken ct);
    }
}
