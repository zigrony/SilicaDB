namespace Silica.Concurrency
{

    public interface ILockRpcClient
    {
        Task<LockGrant> RequestSharedAsync(string nodeId, long txId, string resource, int timeout, CancellationToken ct);
        Task<LockGrant> RequestExclusiveAsync(string nodeId, long txId, string resource, int timeout, CancellationToken ct);
        Task ReleaseAsync(string nodeId, long txId, string resource, long fencingToken, CancellationToken ct);
    }
}