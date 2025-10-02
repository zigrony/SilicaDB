// Path: Silica.Storage.Allocation/IAllocationStrategy.cs
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;

namespace Silica.Storage.Allocation
{
    /// <summary>
    /// Strategy contract (SQL-style, blob-style, columnar, etc.).
    /// Strategies implement IStorageAllocator and declare a stable Name for registration.
    /// </summary>
    public interface IAllocationStrategy : IStorageAllocator
    {
        /// <summary>
        /// Stable identifier used by the registry/factory.
        /// Keep this low-cardinality and human-readable ("sql", "blob", "columnar").
        /// </summary>
        string Name { get; }
    }
}
