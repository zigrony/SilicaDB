using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.BufferPool
{
    /// <summary>
    /// Optional callback for BufferPool to ensure WAL-before-write ordering.
    /// Transactions layer implements this to flush WAL up to a given LSN before a page is written to storage.
    /// </summary>
    public interface IWalFlushCoordinator
    {
        /// <summary>
        /// Ensure that the WAL is flushed to durable storage up to at least <paramref name="lsn"/>.
        /// Must complete before the page with that LSN is written to disk.
        /// </summary>
        ValueTask EnsureWalFlushedAsync(ulong lsn, CancellationToken ct);
    }
}
