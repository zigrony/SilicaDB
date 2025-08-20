// File: SilicaDB.Evictions.Interfaces/AsyncEvictionCacheBase.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Evictions.Interfaces
{
    /// <summary>
    /// Asynchronous eviction‐cache abstraction. 
    /// All state mutations guarded by an AsyncLock. 
    /// </summary>
    public interface IAsyncEvictionCache<TKey, TValue> : IAsyncDisposable
        where TKey : notnull
    {
        /// <summary>
        /// Returns existing value or atomically adds a new one via factory.
        /// May evict old entries under the same lock.
        /// </summary>
        ValueTask<TValue> GetOrAddAsync(TKey key);

        /// <summary>
        /// Triggers idle‐time eviction.
        /// </summary>
        ValueTask CleanupIdleAsync();

        public int Count { get; }
    }

}
