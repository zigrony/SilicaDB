using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    /// <summary>
    /// Eviction by insertion order: oldest added entry evicted first.
    /// </summary>
    public class EvictionFifoCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
        private readonly Queue<TKey> _queue = new();
        private readonly Dictionary<TKey, TValue> _map = new();
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvicted;
        private bool _disposed;
        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        public int Count
        {
            get
            {
                // Snapshot the count under lock for thread-safety.
                lock (_lock)
                {
                    return _map.Count;
                }
            }
        }

        public EvictionFifoCache(
          int capacity,
          Func<TKey, ValueTask<TValue>> factory,
          Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onEvicted = onEvictedAsync ?? throw new ArgumentNullException(nameof(onEvictedAsync));
        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EvictionFifoCache<TKey, TValue>));

            // fast hit
            if (_map.TryGetValue(key, out var val))
                return val;

            // miss → create
            var newVal = await _factory(key).ConfigureAwait(false);

            List<(TKey, TValue)> evicted = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out val))
                    return val;

                // add to queue + map
                _queue.Enqueue(key);
                _map[key] = newVal;

                // capacity check
                if (_map.Count > _capacity)
                {
                    var oldest = _queue.Dequeue();
                    if (_map.Remove(oldest, out var removedVal))
                    {
                        evicted = new List<(TKey, TValue)> { (oldest, removedVal) };
                    }
                }
            }

            // callback outside lock
            if (evicted != null)
                foreach (var (k, v) in evicted)
                    await _onEvicted(k, v).ConfigureAwait(false);

            return newVal;
        }

        public ValueTask CleanupIdleAsync() => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            List<(TKey, TValue)> all = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                all = new List<(TKey, TValue)>(_map.Count);
                foreach (var kv in _map)
                    all.Add((kv.Key, kv.Value));
                _map.Clear();
                _queue.Clear();
            }

            foreach (var (k, v) in all)
                await _onEvicted(k, v).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }



}
