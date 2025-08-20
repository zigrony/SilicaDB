// LocalLockRpcClient.cs
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Concurrency
{
    /// <summary>
    /// In-process stub for ILockRpcClient that delegates back to the same LockManager.
    /// </summary>
    internal class LocalLockRpcClient : ILockRpcClient
    {
        private readonly LockManager _mgr;
        public LocalLockRpcClient(LockManager mgr) => _mgr = mgr;

        public Task<LockGrant> RequestSharedAsync(
            string nodeId,
            long txId,
            string resource,
            int timeout,
            CancellationToken ct)
        {
            return _mgr.AcquireSharedLocalAsync(txId, resource, timeout, ct);
        }

        public Task<LockGrant> RequestExclusiveAsync(
            string nodeId,
            long txId,
            string resource,
            int timeout,
            CancellationToken ct)
        {
            return _mgr.AcquireExclusiveLocalAsync(txId, resource, timeout, ct);
        }

        public Task ReleaseAsync(
            string nodeId,
            long txId,
            string resource,
            long fencingToken)
        {
            _mgr.ReleaseLocal(txId, resource, fencingToken);
            return Task.CompletedTask;
        }
    }
}
