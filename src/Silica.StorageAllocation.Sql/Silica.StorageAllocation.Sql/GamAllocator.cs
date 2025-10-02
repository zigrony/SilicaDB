using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;

namespace Silica.StorageAllocation.Sql
{
    /// <summary>
    /// Helper for scanning and flipping GAM bits.
    /// Stubbed for now.
    /// </summary>
    public sealed class GamAllocator
    {
        private readonly IBufferPoolManager _pool;
        private readonly SqlAllocationManifest _manifest;

        public GamAllocator(IBufferPoolManager pool, SqlAllocationManifest manifest)
        {
            _pool = pool;
            _manifest = manifest;
        }

        public Task<int> FindFreeExtentIndexAsync(CancellationToken ct = default)
        {
            // TODO: scan GAM pages for free bit
            return Task.FromResult(0);
        }

        public Task FlipGamBitAsync(int extentIndex, CancellationToken ct = default)
        {
            // TODO: mark extent allocated
            return Task.CompletedTask;
        }
    }
}
