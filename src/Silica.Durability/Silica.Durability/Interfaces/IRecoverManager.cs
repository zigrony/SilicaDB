using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Durability
{
    /// <summary>
    /// Async, thread-safe, single-consumer WAL replay interface.
    /// </summary>
    public interface IRecoverManager : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Opens (or creates) the WAL file and seeks to start.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Reads the next record; returns null at EOF.
        /// </summary>
        Task<WalRecord?> ReadNextAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops reading. Subsequent ReadNextAsync calls will throw.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken);
    }
}
