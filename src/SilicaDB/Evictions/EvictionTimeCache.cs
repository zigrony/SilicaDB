using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    public class EvictionTimeCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly long _idleTicks;
        private readonly Func<long> _now;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map
            = new();
        private readonly LinkedList<CacheEntry> _lruList
            = new();
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvictedAsync;
        private bool _disposed;
        private int _count;
        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        private sealed class CacheEntry
        {
            public TKey Key { get; }
            public TValue Value { get; }
            /// <summary>
            /// The absolute Stopwatch-ticks when this entry should be pulled.
            /// </summary>
            public long Expiration { get; set; }

            public CacheEntry(TKey key, TValue value, long now, long idleTicks)
            {
                Key = key;
                Value = value;
                Expiration = now + idleTicks;
            }
        }

        /// <param name="timeProvider">
        ///   Defaults to Stopwatch.GetTimestamp.  In tests you can
        ///   pass a fake that you advance manually.
        /// </param>
        public EvictionTimeCache(
            TimeSpan idleTimeout,
            Func<TKey, ValueTask<TValue>> factory,
            Func<TKey, TValue, ValueTask> onEvictedAsync,
            Func<long>? timeProvider = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onEvictedAsync = onEvictedAsync ?? throw new ArgumentNullException(nameof(onEvictedAsync));
            _now = timeProvider ?? Stopwatch.GetTimestamp;

            // Convert idleTimeout (100 ns ticks) → Stopwatch ticks
            _idleTicks = idleTimeout.Ticks * Stopwatch.Frequency
                         / TimeSpan.TicksPerSecond;
        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EvictionTimeCache<TKey, TValue>));

            var now = _now();

            // Phase 1: try a hit
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // push expiration forward
                    node.Value.Expiration = now + _idleTicks;
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return node.Value.Value;
                }
            }

            // Phase 2: build the value
            var newValue = await _factory(key).ConfigureAwait(false);

            // Phase 3: re-check and insert
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var existing))
                    return existing.Value.Value;

                var entry = new CacheEntry(key, newValue, now, _idleTicks);
                var node = new LinkedListNode<CacheEntry>(entry);
                _lruList.AddFirst(node);
                _map[key] = node;
                Interlocked.Increment(ref _count);
            }

            return newValue;
        }

        public async ValueTask CleanupIdleAsync()
        {
            var now = _now();
            var toEvict = new List<CacheEntry>();

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // Evict all expired entries at the tail
                while (true)
                {
                    var tail = _lruList.Last;
                    if (tail == null || tail.Value.Expiration >= now)
                        break;

                    _lruList.RemoveLast();
                    _map.Remove(tail.Value.Key);
                    Interlocked.Decrement(ref _count);
                    toEvict.Add(tail.Value);
                }
            }

            // Fire callbacks outside the lock
            foreach (var e in toEvict)
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
                Interlocked.Exchange(ref _count, 0);
            }

            foreach (var e in allEntries)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }
}
