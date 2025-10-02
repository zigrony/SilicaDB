using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Common.Primitives;

namespace Silica.StorageAllocation.Sql
{
    /// <summary>
    /// Helper for tracking per-page free space (PFS).
    /// Stubbed for now.
    /// </summary>
    public sealed class PfsTracker
    {
        private readonly IBufferPoolManager _pool;
        private readonly SqlAllocationManifest _manifest;

        public PfsTracker(IBufferPoolManager pool, SqlAllocationManifest manifest)
        {
            _pool = pool;
            _manifest = manifest;
        }

        public Task MarkPageAllocatedAsync(PageId pageId, CancellationToken ct = default)
        {
            // TODO: update PFS entry
            return Task.CompletedTask;
        }

        public Task MarkPageFreeAsync(PageId pageId, CancellationToken ct = default)
        {
            // TODO: update PFS entry
            return Task.CompletedTask;
        }
    }
}
