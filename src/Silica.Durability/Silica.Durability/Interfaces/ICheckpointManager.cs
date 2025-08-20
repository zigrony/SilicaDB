// File: ICheckpointManager.cs
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Durability
{
    /// <summary>
    /// Coordinates writing and reading durable checkpoints for WAL-based recovery.
    /// </summary>
    public interface ICheckpointManager : IAsyncDisposable
    {
        /// <summary>
        /// Prepare checkpoint directory, scan for existing checkpoint files.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically write a new checkpoint (LSN + timestamp).
        /// </summary>
        Task WriteCheckpointAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Load the most recent checkpoint, or null if none exists.
        /// </summary>
        Task<CheckpointData?> ReadLatestCheckpointAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Graceful shutdown. (Optional alias for DisposeAsync.)
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default)
            => DisposeAsync().AsTask();
    }
}
