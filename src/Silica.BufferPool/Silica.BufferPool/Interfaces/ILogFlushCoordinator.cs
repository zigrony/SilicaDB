// Filename: Silica.BufferPool/Interfaces/ILogFlushCoordinator.cs
using System.Threading;
using System.Threading.Tasks;

namespace Silica.BufferPool
{
    /// <summary>
    /// Transaction/WAL coordination: ensures the log is durably flushed
    /// to at least the provided LSN before a page is written.
    /// </summary>
    public interface ILogFlushCoordinator
    {
        /// <summary>
        /// Ensure the log is flushed to at least 'lsn'.
        /// Must return when the durability point is reached or throw on failure/cancellation.
        /// </summary>
        Task EnsureFlushedAsync(long lsn, CancellationToken ct = default);
    }
}
