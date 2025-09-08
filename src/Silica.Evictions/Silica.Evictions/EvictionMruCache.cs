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
    /// Eviction of the most-recently-used entry on overflow.
    /// The head of the list is always the most recent, so we
    /// discard it first when capacity is exceeded.
    /// </summary>
    public class EvictionMruCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvicted;

        // MRU list: head = most recent, tail = least
        private readonly LinkedList<TKey> _mruList = new();
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes
            = new Dictionary<TKey, LinkedListNode<TKey>>();
        private readonly Dictionary<TKey, TValue> _values
            = new Dictionary<TKey, TValue>();

        // Tracks current entry count for metrics
        private int _count;

        private bool _disposed;
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;

        public EvictionMruCache(
            int capacity,
            Func<TKey, ValueTask<TValue>> factory,
            Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));

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

        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EvictionMruCache<TKey, TValue>));

            // Fast-path hit: bump to front
            if (_values.TryGetValue(key, out var existing))
            {
                using (await _lock.LockAsync().ConfigureAwait(false))
                {
                    var node = _nodes[key];
                    _mruList.Remove(node);
                    _mruList.AddFirst(node);
                }
                try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                return existing;
            }

            // Miss → build value outside lock
            var swFactory = Stopwatch.StartNew();
            var newValue = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            try { EvictionMetrics.IncrementMiss(_metrics); } catch { }
            (TKey Key, TValue Value)? evicted = null;

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // Re-check in case another thread inserted
                if (_values.ContainsKey(key))
                {
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return _values[key];
                }

                // If full, evict the most-recent (head of list)
                if (Volatile.Read(ref _count) >= _capacity)
                {
                    var mruNode = _mruList.First!;
                    var mruKey = mruNode.Value;
                    var mruVal = _values[mruKey];

                    _mruList.RemoveFirst();
                    _nodes.Remove(mruKey);
                    _values.Remove(mruKey);

                    Interlocked.Decrement(ref _count);
                    evicted = (mruKey, mruVal);
                    try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Mru); } catch { }
                }

                // Insert new at head
                var newNode = new LinkedListNode<TKey>(key);
                _mruList.AddFirst(newNode);
                _nodes[key] = newNode;
                _values[key] = newValue;

                Interlocked.Increment(ref _count);
            }

            // Fire eviction callback outside the lock
            if (evicted.HasValue)
                await _onEvicted(evicted.Value.Key, evicted.Value.Value).ConfigureAwait(false);

            return newValue;
        }

        public ValueTask CleanupIdleAsync() =>
            ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            List<(TKey, TValue)> all;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                all = new List<(TKey, TValue)>(_values.Count);
                foreach (var kv in _values)
                    all.Add((kv.Key, kv.Value));

                _values.Clear();
                _nodes.Clear();
                _mruList.Clear();

                Interlocked.Exchange(ref _count, 0);
            }

            foreach (var (k, v) in all)
                await _onEvicted(k, v).ConfigureAwait(false);

            _lock.Dispose();
        }
    }
}
