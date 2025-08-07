// File: IWalManager.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Durability
{
    public interface IWalManager : IAsyncDisposable, IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task AppendAsync(WalRecord record, CancellationToken cancellationToken);
        Task FlushAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
        Task<long> GetLastSequenceNumberAsync(CancellationToken cancellationToken = default);
    }
}
