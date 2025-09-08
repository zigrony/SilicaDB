using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Silica.Evictions.Interfaces;
using System.Diagnostics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Evictions.Metrics;

namespace Silica.Evictions
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
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;

        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        private int _count;
        public int Count => Volatile.Read(ref _count);


        public EvictionFifoCache(
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
            if (_disposed) throw new ObjectDisposedException(nameof(EvictionFifoCache<TKey, TValue>));

            // fast hit
            if (_map.TryGetValue(key, out var val))
            {
                try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                return val;
            }

            // miss → create
            var swFactory = Stopwatch.StartNew();
            var newVal = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            try { EvictionMetrics.IncrementMiss(_metrics); } catch { }

            List<(TKey, TValue)> evicted = null;
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out val))
                {
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return val;
                }

                // add to queue + map
                _queue.Enqueue(key);
                _map[key] = newVal;
                Interlocked.Increment(ref _count);

                // capacity check
                if (Volatile.Read(ref _count) > _capacity)
                {
                    var oldest = _queue.Dequeue();
                    if (_map.Remove(oldest, out var removedVal))
                    {
                        Interlocked.Decrement(ref _count);
                        evicted = new List<(TKey, TValue)> { (oldest, removedVal) };
                        try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.SizeOnly); } catch { }
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
                Interlocked.Exchange(ref _count, 0);
            }

            foreach (var (k, v) in all)
                await _onEvicted(k, v).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }



}
