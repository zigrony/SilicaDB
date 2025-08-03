// File: SilicaDB.Evictions/EvictionTimeCache.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    /// <summary>
    /// Idle‐only eviction. No capacity limit; entries expire after idleTimeout.
    /// </summary>
    public class EvictionTimeCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly TimeSpan _idleTimeout;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lruList;
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvictedAsync;
        private bool _disposed;

        private sealed class CacheEntry
        {
            public TKey Key { get; }
            public TValue Value { get; }
            public DateTime LastUse { get; set; }

            public CacheEntry(TKey key, TValue value)
            {
                Key = key;
                Value = value;
                LastUse = DateTime.UtcNow;
            }
        }

        public EvictionTimeCache(
            TimeSpan idleTimeout,
            Func<TKey, ValueTask<TValue>> factory,
            Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            _idleTimeout = idleTimeout;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onEvictedAsync = onEvictedAsync ?? throw new ArgumentNullException(nameof(onEvictedAsync));

            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>();
            _lruList = new LinkedList<CacheEntry>();
        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EvictionLruCache<TKey, TValue>));

            // Phase 1: existing‐entry hit
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var node))
                {
                    node.Value.LastUse = DateTime.UtcNow;
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return node.Value.Value;
                }
            }

            // Phase 2: build value outside lock
            var createdValue = await _factory(key).ConfigureAwait(false);

            // Phase 3: insert new entry under lock
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var existingNode2))
                    return existingNode2.Value.Value;

                var entry = new CacheEntry(key, createdValue);
                var node2 = new LinkedListNode<CacheEntry>(entry);
                _lruList.AddFirst(node2);
                _map[key] = node2;
            }

            // Phase 4: done
            return createdValue;
        }

        public async ValueTask CleanupIdleAsync()
        {
            var threshold = DateTime.UtcNow - _idleTimeout;
            List<CacheEntry>? toEvict = null;

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                toEvict = new List<CacheEntry>();
                while (_lruList.Last is { Value: var entry } tailNode
                       && entry.LastUse < threshold)
                {
                    _lruList.RemoveLast();
                    _map.Remove(entry.Key);
                    toEvict.Add(entry);
                }
            }

            foreach (var e in toEvict!)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);
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
            }

            foreach (var e in allEntries)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);
        }
    }
}
