using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Evictions.Interfaces;
using System.Diagnostics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Evictions.Metrics;

namespace Silica.Evictions
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
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;

        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        private int _count;
        public int Count => Volatile.Read(ref _count);

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
            // Metrics init + registration
            _componentName = GetType().Name;
            _metrics = DiagnosticsCoreBootstrap.IsStarted ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager();
            try
            {
                if (!_metricsRegistered)
                {
                    EvictionMetrics.RegisterAll(
                        _metrics,
                        cacheComponentName: _componentName,
                        entriesProvider: () => Count,
                        capacityProvider: () => _capacity);
                    _metricsRegistered = DiagnosticsCoreBootstrap.IsStarted;
                }
            }
            catch { /* swallow */ }

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
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return val;
                }
                hit = false;
            }

            // miss → build
            var swFactory = Stopwatch.StartNew();
            val = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            try { EvictionMetrics.IncrementMiss(_metrics); } catch { }
            newFreq = 1;

            List<(TKey, TValue)> evicted = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.ContainsKey(key))
                {
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return _map[key].Value;
                }

                // insert at freq=1 bucket
                _map[key] = (val, 1);
                Interlocked.Increment(ref _count);
                if (!_buckets.TryGetValue(1, out var bucket1))
                {
                    bucket1 = new LinkedList<TKey>();
                    _buckets[1] = bucket1;
                }
                bucket1.AddLast(key);

                if (Volatile.Read(ref _count) > _capacity)
                {
                    // Evict lowest-frequency, oldest in that bucket (no LINQ)
                    int lowestFreq = 0;
                    LinkedList<TKey>? lowestList = null;
                    using (var enumerator = _buckets.GetEnumerator())
                    {
                        if (enumerator.MoveNext())
                        {
                            var kvp = enumerator.Current;
                            lowestFreq = kvp.Key;
                            lowestList = kvp.Value;
                        }
                    }
                    if (lowestList is not null && lowestList.First is not null)
                    {
                        var oldestKey = lowestList.First.Value;
                        lowestList.RemoveFirst();
                        if (lowestList.Count == 0)
                            _buckets.Remove(lowestFreq);

                        var removedVal = _map[oldestKey].Value;
                        _map.Remove(oldestKey);
                        Interlocked.Decrement(ref _count);
                        evicted = new List<(TKey, TValue)> { (oldestKey, removedVal) };
                        // LFU policy eviction
                        try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Lfu); } catch { }
                    }
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
                Interlocked.Exchange(ref _count, 0);
            }

            foreach (var (k, v) in all)
                await _onEvicted(k, v).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();


        }
    }
}