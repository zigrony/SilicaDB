using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Common.Primitives;

namespace Silica.Common.Contracts
{
    /// <summary>
    /// Contract for write-ahead logging hooks.
    /// Implemented by PageAccess, consumed by StorageAllocation.
    /// </summary>
    public interface IPageWalHook
    {
        ValueTask<ulong> OnBeforePageWriteAsync(
            PageId pageId,
            int offset,
            int length,
            ReadOnlyMemory<byte> afterImage,
            CancellationToken ct);
    }
}
