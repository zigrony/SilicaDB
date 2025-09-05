// File: IWalManager.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Durability
{
    public interface IWalManager : IAsyncDisposable, IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);
        /// <summary>
        /// Appends the given payload to the WAL. The WAL assigns the persisted LSN;
        /// any value in <see cref="WalRecord.SequenceNumber"/> is ignored by the writer.
        /// The highest successfully written LSN can be read via <see cref="GetLastSequenceNumberAsync"/>.
        /// </summary>
        Task AppendAsync(WalRecord record, CancellationToken cancellationToken);
        Task FlushAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
        Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default);
        /// <summary>
        /// Returns the highest LSN that is durably flushed to stable storage.
        /// </summary>
        Task<long> GetFlushedSequenceNumberAsync(CancellationToken cancellationToken = default);

    }
}
