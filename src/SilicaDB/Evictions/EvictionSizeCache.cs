// File: SilicaDB.Evictions/EvictionSizeCache.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    /// <summary>
    /// Capacity‐only eviction. No time‐based cleanup.
    /// </summary>
    public class EvictionSizeCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lruList;
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvictedAsync;
        private bool _disposed;
        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        private int _count;
        public int Count => Volatile.Read(ref _count);

        private sealed class CacheEntry
        {
            public TKey Key { get; }
            public TValue Value { get; }

            public CacheEntry(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
        }

        public EvictionSizeCache(
            int capacity,
            Func<TKey, ValueTask<TValue>> factory,
            Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onEvictedAsync = onEvictedAsync ?? throw new ArgumentNullException(nameof(onEvictedAsync));

            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(capacity);
            _lruList = new LinkedList<CacheEntry>();
        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EvictionSizeCache<TKey, TValue>));

            // Phase 1: hit check
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return node.Value.Value;
                }
            }

            // Phase 2: build value
            var createdValue = await _factory(key).ConfigureAwait(false);

            // Phase 3: insert & maybe evict
            List<CacheEntry>? toEvict = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var existingNode2))
                    return existingNode2.Value.Value;

                var entry = new CacheEntry(key, createdValue);
                var node1 = new LinkedListNode<CacheEntry>(entry);
                _lruList.AddFirst(node1);
                _map[key] = node1;
                Interlocked.Increment(ref _count);

                if (_map.Count > _capacity)
                {
                    toEvict = new List<CacheEntry>(1);
                    var tail = _lruList.Last!;
                    _lruList.RemoveLast();
                    _map.Remove(tail.Value.Key);
                    Interlocked.Decrement(ref _count);
                    toEvict.Add(tail.Value);
                }
            }

            // Phase 4: callbacks outside lock
            if (toEvict != null)
                foreach (var e in toEvict)
                    await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);

            return createdValue;
        }

        public ValueTask CleanupIdleAsync()
        {
            // No time‐based eviction
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            List<CacheEntry> allEntries;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                allEntries = new List<CacheEntry>(_lruList);
                _lruList.Clear();
                _map.Clear();
                Interlocked.Exchange(ref _count, 0);
            }

            foreach (var e in allEntries)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }
}
