using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.Storage.Allocation;
using Silica.Storage.Allocation.Exceptions;
using Silica.StorageAllocation.Sql.Diagnostics;
using Silica.Common.Primitives;

namespace Silica.StorageAllocation.Sql
{
    /// <summary>
    /// SQL-style allocation strategy using GAM/SGAM/PFS semantics.
    /// Stubbed for now; will delegate to helpers later.
    /// </summary>
    public sealed class SqlAllocationStrategy : IAllocationStrategy
    {
        public string Name => "sql";

        private readonly IBufferPoolManager _pool;
        private readonly SqlAllocationManifest _manifest;
        private readonly GamAllocator _gam;
        private readonly PfsTracker _pfs;

        public SqlAllocationStrategy(IBufferPoolManager pool, SqlAllocationManifest manifest)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _gam = new GamAllocator(pool, manifest);
            _pfs = new PfsTracker(pool, manifest);
        }

        public async Task<PageId> AllocatePageAsync(CancellationToken ct = default)
        {
            SqlAllocationDiagnostics.Emit("AllocatePage", "info", "Stubbed page allocation invoked.");
            // Stub: delegate to extent allocation for now
            return await AllocateExtentAsync(ct).ConfigureAwait(false);
        }

        public async Task<PageId> AllocateExtentAsync(CancellationToken ct = default)
        {
            SqlAllocationDiagnostics.Emit("AllocateExtent", "info", "Stubbed extent allocation invoked.");
            // Stub: return a dummy PageId
            return new PageId(FileId: 1, PageIndex: 0);
        }

        public Task FreePageAsync(PageId pageId, CancellationToken ct = default)
        {
            SqlAllocationDiagnostics.Emit("FreePage", "info", $"Stubbed free invoked for {pageId}.");
            return Task.CompletedTask;
        }
    }
}
