using System.Threading;
using System.Threading.Tasks;
using Silica.Common.Primitives;
using Silica.Storage.Allocation;

namespace Silica.StorageAllocation
{
    public sealed class StubAllocator : IStorageAllocator
    {
        private long _next = 1;

        public Task<PageId> AllocatePageAsync(CancellationToken ct = default) =>
            Task.FromResult(new PageId(1, Interlocked.Increment(ref _next)));

        public Task<PageId> AllocateExtentAsync(CancellationToken ct = default) =>
            Task.FromResult(new PageId(1, Interlocked.Increment(ref _next)));

        public Task FreePageAsync(PageId pageId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
