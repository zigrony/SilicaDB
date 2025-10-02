// Path: Silica.Storage.Allocation/StorageAllocator.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Common.Primitives;

namespace Silica.Storage.Allocation
{
    /// <summary>
    /// Host that delegates to a chosen allocation strategy.
    /// PageAccess and higher layers should depend on this type.
    /// </summary>
    public sealed class StorageAllocator : IStorageAllocator
    {
        private readonly IAllocationStrategy _strategy;

        public StorageAllocator(IAllocationStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public Task<PageId> AllocatePageAsync(CancellationToken ct = default)
            => _strategy.AllocatePageAsync(ct);

        public Task<PageId> AllocateExtentAsync(CancellationToken ct = default)
            => _strategy.AllocateExtentAsync(ct);

        public Task FreePageAsync(PageId pageId, CancellationToken ct = default)
            => _strategy.FreePageAsync(pageId, ct);
    }
}
