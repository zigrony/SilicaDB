using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    /// <summary>
    /// Eviction of the least-frequently-used key.
    /// Maintains a frequency map and bucket lists.
    /// </summary>
    public class EvictionLfuCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
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

        // key → (value, freq)
        private readonly Dictionary<TKey, (TValue Value, int Freq)> _map
          = new Dictionary<TKey, (TValue, int)>();
        // freq → set of keys
        private readonly SortedDictionary<int, LinkedList<TKey>> _buckets
          = new SortedDictionary<int, LinkedList<TKey>>();

        public EvictionLfuCache(
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
            if (_disposed) throw new ObjectDisposedException(nameof(EvictionLfuCache<TKey, TValue>));

            TValue val;
            int oldFreq, newFreq;
            bool hit;

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var tuple))
                {
                    // increment frequency
                    (val, oldFreq) = tuple;
                    newFreq = oldFreq + 1;
                    _map[key] = (val, newFreq);

                    // move key from old bucket → new bucket
                    var list = _buckets[oldFreq];
                    list.Remove(key);
                    if (list.Count == 0) _buckets.Remove(oldFreq);

                    if (!_buckets.TryGetValue(newFreq, out list))
                    {
                        list = new LinkedList<TKey>();
                        _buckets[newFreq] = list;
                    }
                    list.AddLast(key);

                    return val;
                }
                hit = false;
            }

            // miss → build
            val = await _factory(key).ConfigureAwait(false);
            newFreq = 1;

            List<(TKey, TValue)> evicted = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.ContainsKey(key))
                    return _map[key].Value;

                // insert at freq=1 bucket
                _map[key] = (val, 1);
                if (!_buckets.TryGetValue(1, out var bucket1))
                {
                    bucket1 = new LinkedList<TKey>();
                    _buckets[1] = bucket1;
                }
                bucket1.AddLast(key);

                if (_map.Count > _capacity)
                {
                    // evict lowest-frequency, oldest in that bucket
                    var lowest = _buckets.First();
                    var oldestKey = lowest.Value.First.Value;
                    lowest.Value.RemoveFirst();
                    if (lowest.Value.Count == 0)
                        _buckets.Remove(lowest.Key);

                    var removedVal = _map[oldestKey].Value;
                    _map.Remove(oldestKey);

                    evicted = new List<(TKey, TValue)> { (oldestKey, removedVal) };
                }
            }

            if (evicted != null)
                foreach (var (k, v) in evicted)
                    await _onEvicted(k, v).ConfigureAwait(false);

            return val;
        }

        public ValueTask CleanupIdleAsync() => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            List<(TKey, TValue)> all = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                all = new List<(TKey, TValue)>();
                foreach (var kv in _map)
                    all.Add((kv.Key, kv.Value.Value));
                _map.Clear();
                _buckets.Clear();
            }

            foreach (var (k, v) in all)
                await _onEvicted(k, v).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();


        }
    }
}