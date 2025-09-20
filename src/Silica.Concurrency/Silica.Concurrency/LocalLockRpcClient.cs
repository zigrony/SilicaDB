// LocalLockRpcClient.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Concurrency
{
    /// <summary>
    /// In-process stub for ILockRpcClient that delegates back to the same LockManager.
    /// </summary>
    internal sealed class LocalLockRpcClient : ILockRpcClient
    {
        private readonly LockManager _mgr;
        public LocalLockRpcClient(LockManager mgr) => _mgr = mgr ?? throw new ArgumentNullException(nameof(mgr));

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
            long fencingToken,
            CancellationToken ct)
        {
            // Release is non-cancellable (ignore ct) to avoid leaks.
            // Surface failures as a faulted Task (async contract consistency).
            try
            {
                _mgr.ReleaseLocal(txId, resource, fencingToken);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                // Avoid using Task.FromException to keep within base framework surface area
                // while maintaining no-linq/no-reflection policy.
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    tcs.SetException(ex);
                }
                catch { }
                return tcs.Task;
            }
        }
    }
}
