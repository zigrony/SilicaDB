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
    /// Eviction by “clock” (second-chance) algorithm:
    /// entries are arranged in a circular buffer with a reference bit.
    /// On miss, we scan: if ref-bit == false, evict; else clear bit and advance.
    /// Provides an O(1) approximation of LRU with lower overhead.
    /// </summary>
    public class EvictionClockCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly AsyncLock _lock = new();
        private readonly int _capacity;
        private readonly Func<TKey, ValueTask<TValue>> _factory;
        private readonly Func<TKey, TValue, ValueTask> _onEvicted;
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;

        private struct Entry
        {
            public TKey Key;
            public TValue Value;
            public bool ReferenceBit;
            public bool Occupied;
        }

        private readonly Entry[] _entries;
        private readonly Dictionary<TKey, int> _map;
        private int _hand;
        private bool _disposed;

        /// <summary>
        /// How many entries are currently in the cache.
        /// </summary>
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public EvictionClockCache(
          int capacity,
          Func<TKey, ValueTask<TValue>> factory,
          Func<TKey, TValue, ValueTask> onEvictedAsync)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onEvicted = onEvictedAsync ?? throw new ArgumentNullException(nameof(onEvictedAsync));

            _entries = new Entry[capacity];
            _map = new Dictionary<TKey, int>(capacity);
            _hand = 0;
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
                throw new ObjectDisposedException(nameof(EvictionClockCache<TKey, TValue>));

            // Fast-path hit: mark reference bit
            if (_map.TryGetValue(key, out var idx))
            {
                using (await _lock.LockAsync().ConfigureAwait(false))
                {
                    var e = _entries[idx];
                    e.ReferenceBit = true;
                    _entries[idx] = e;
                    // hit
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return e.Value;
                }
            }

            // Miss: build value outside lock (measure latency)
            var swFactory = Stopwatch.StartNew();
            var newValue = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            bool recordedMiss = false;
            try { EvictionMetrics.IncrementMiss(_metrics); recordedMiss = true; } catch { }
            (TKey evKey, TValue evValue)? toEvict = null;

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // Another thread may have inserted
                if (_map.TryGetValue(key, out idx))
                {
                    var e = _entries[idx];
                    e.ReferenceBit = true;
                    _entries[idx] = e;
                    // Another thread satisfied the miss; treat as hit
                    if (recordedMiss)
                    {
                        // Optional: leave as-is to avoid double-account; simple approach: do nothing.
                    }
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }

                    return e.Value;
                }

                // If not full, find next free slot
                if (Volatile.Read(ref _count) < _capacity)
                {
                    idx = _hand;
                    while (_entries[idx].Occupied)
                        idx = (idx + 1) % _capacity;

                    _entries[idx] = new Entry
                    {
                        Key = key,
                        Value = newValue,
                        ReferenceBit = true,
                        Occupied = true
                    };
                    _map[key] = idx;
                    Interlocked.Increment(ref _count);
                    _hand = (idx + 1) % _capacity;
                }
                else
                {
                    // Clock algorithm: scan for eviction
                    long cleared = 0;
                    while (true)
                    {
                        var candidate = _entries[_hand];

                        if (!candidate.ReferenceBit)
                        {
                            // Evict this slot
                            toEvict = (candidate.Key, candidate.Value);
                            _map.Remove(candidate.Key);
                            // adjust counter for removal
                            Interlocked.Decrement(ref _count);

                            _entries[_hand] = new Entry
                            {
                                Key = key,
                                Value = newValue,
                                ReferenceBit = true,
                                Occupied = true
                            };
                            _map[key] = _hand;

                            idx = _hand;
                            _hand = (idx + 1) % _capacity;
                            Interlocked.Increment(ref _count);
                            // eviction due to CLOCK
                            try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Clock); } catch { }
                            if (cleared > 0) { try { EvictionMetrics.IncrementClockSecondChanceClears(_metrics, cleared); } catch { } }
                            break;
                        }
                        else
                        {
                            // Give second chance
                            candidate.ReferenceBit = false;
                            _entries[_hand] = candidate;
                            _hand = (_hand + 1) % _capacity;
                            cleared++;
                        }
                    }
                }
            }

            // Invoke eviction callback outside lock
            if (toEvict.HasValue)
                await _onEvicted(toEvict.Value.evKey, toEvict.Value.evValue)
                         .ConfigureAwait(false);

            return newValue;
        }

        public ValueTask CleanupIdleAsync() =>
          ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var allItems = new List<(TKey, TValue)>(_capacity);
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                foreach (var e in _entries)
                    if (e.Occupied)
                        allItems.Add((e.Key, e.Value));

                _map.Clear();
                Interlocked.Exchange(ref _count, 0);
                for (int i = 0; i < _entries.Length; i++)
                    _entries[i] = default;
            }

            foreach (var (k, v) in allItems)
                await _onEvicted(k, v).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }
}
