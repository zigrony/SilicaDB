// File: SilicaDB.Evictions/EvictionLruCache.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Silica.Evictions.Interfaces;
using System.Diagnostics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Evictions.Metrics;
using Silica.Evictions.Exceptions;

namespace Silica.Evictions
{
    /// <summary>
    /// Capacity‐and‐idle based LRU cache. 
    /// Evicts oldest on Add when over capacity, and stale on idle cleanup.
    /// </summary>
    public class EvictionLruCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
        private readonly TimeSpan _idleTimeout;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lruList;
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvictedAsync;
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

        public EvictionLruCache(
            int capacity,
            TimeSpan idleTimeout,
            Func<TKey, ValueTask<TValue>> factory,
            Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            if (capacity < 1) throw new EvictionInvalidCapacityException(capacity);
            _capacity = capacity;
            _idleTimeout = idleTimeout;
            _factory = factory ?? throw new EvictionNullValueFactoryException();
            _onEvictedAsync = onEvictedAsync ?? throw new EvictionNullOnEvictedException();

            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(capacity);
            _lruList = new LinkedList<CacheEntry>();
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
            catch { /* swallow exporter/registration issues */ }
        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new EvictionDisposedException();

            // Phase 1: quick hit‐check under lock
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var existingNode))
                {
                    existingNode.Value.LastUse = DateTime.UtcNow;
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return existingNode.Value.Value;
                }
            }

            // Phase 2: create value outside lock
            var swFactory = Stopwatch.StartNew();
            var createdValue = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            try { EvictionMetrics.IncrementMiss(_metrics); } catch { }

            // Phase 3: re-acquire lock to insert + collect eviction
            List<CacheEntry>? toEvict = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // In the meantime another thread may have added it
                if (_map.TryGetValue(key, out var existingNode2))
                {
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return existingNode2.Value.Value;
                }

                var entry = new CacheEntry(key, createdValue);
                var node = new LinkedListNode<CacheEntry>(entry);
                _lruList.AddFirst(node);
                _map[key] = node;
                Interlocked.Increment(ref _count);

                if (Volatile.Read(ref _count) > _capacity)
                {
                    toEvict = new List<CacheEntry>(1);
                    var tail = _lruList.Last!;
                    _lruList.RemoveLast();
                    _map.Remove(tail.Value.Key);
                    Interlocked.Decrement(ref _count);
                    toEvict.Add(tail.Value);
                    try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Lru); } catch { }
                }
            }

            // Phase 4: perform onEvicted callbacks outside lock
            if (toEvict != null)
                foreach (var e in toEvict)
                    await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);

            return createdValue;
        }

        public async ValueTask CleanupIdleAsync()
        {
            List<CacheEntry>? toEvict = null;
            var threshold = DateTime.UtcNow - _idleTimeout;
            var swCleanup = Stopwatch.StartNew();

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                toEvict = new List<CacheEntry>();
                while (_lruList.Last is { Value: var entry } tailNode
                       && entry.LastUse < threshold)
                {
                    _lruList.RemoveLast();
                    _map.Remove(entry.Key);
                    Interlocked.Decrement(ref _count);
                    toEvict.Add(entry);
                    try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Time); } catch { }
                }
            }

            foreach (var e in toEvict!)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);
            swCleanup.Stop();
            try { EvictionMetrics.OnCleanupCompleted(_metrics, swCleanup.Elapsed.TotalMilliseconds); } catch { }
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
