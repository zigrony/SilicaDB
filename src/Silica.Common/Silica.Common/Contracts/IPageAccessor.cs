using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using Silica.Common.Primitives;

namespace Silica.Common.Contracts
{
    /// <summary>
    /// Minimal contract for accessing and mutating pages.
    /// Implemented by PageAccess, consumed by StorageAllocation.
    /// </summary>
    public interface IPageAccessor
    {
        ValueTask<object> ReadAsync(PageId id, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default);
        ValueTask<object> WriteAsync(PageId id, int offset, int length, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default);
        ValueTask<object> WriteHeaderAsync(PageId id, PageVersion expectedVersion, PageType expectedType, Func<object, object> mutate, CancellationToken ct = default);
    }
}
