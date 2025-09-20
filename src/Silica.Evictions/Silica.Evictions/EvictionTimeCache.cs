using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Silica.Evictions.Interfaces;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Evictions.Metrics;
using Silica.Evictions.Exceptions;
using Silica.Evictions.Diagnostics;

namespace Silica.Evictions
{
    public class EvictionTimeCache<TKey, TValue> : IAsyncEvictionCache<TKey, TValue>
        where TKey : notnull
    {
        static EvictionTimeCache()
        {
            try { EvictionExceptions.RegisterAll(); } catch { }
        }
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
        // DiagnosticsCore
        private IMetricsManager _metrics;
        private readonly string _componentName;
        private bool _metricsRegistered;
        private readonly bool _verbose;


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
            _factory = factory ?? throw new EvictionNullValueFactoryException();
            _onEvictedAsync = onEvictedAsync ?? throw new EvictionNullOnEvictedException();
            if (idleTimeout < TimeSpan.Zero) throw new EvictionInvalidIdleTimeoutException(idleTimeout);
            _now = timeProvider ?? Stopwatch.GetTimestamp;

            // Convert idleTimeout (100 ns ticks) → Stopwatch ticks
            _idleTicks = idleTimeout.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            // Metrics init + registration
            _componentName = GetType().Name;
            _verbose = EvictionDiagnostics.EnableVerbose;
            _metrics = DiagnosticsCoreBootstrap.IsStarted ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager();
            try
            {
                if (!_metricsRegistered)
                {
                    EvictionMetrics.RegisterAll(
                        _metrics,
                        cacheComponentName: _componentName,
                        entriesProvider: () => Count,
                        capacityProvider: null);
                    _metricsRegistered = DiagnosticsCoreBootstrap.IsStarted;
                }
            }
            catch { /* swallow */ }

        }

        public async ValueTask<TValue> GetOrAddAsync(TKey key)
        {
            if (_disposed)
                throw new EvictionDisposedException();

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
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    if (_verbose) { try { EvictionDiagnostics.EmitDebug(_componentName, "get_or_add", "hit"); } catch { } }
                    return node.Value.Value;
                }
            }

            // Phase 2: build the value
            var swFactory = Stopwatch.StartNew();
            var newValue = await _factory(key).ConfigureAwait(false);
            swFactory.Stop();
            try { EvictionMetrics.RecordFactoryLatency(_metrics, swFactory.Elapsed.TotalMilliseconds); } catch { }
            try { EvictionMetrics.IncrementMiss(_metrics); } catch { }
            if (_verbose) { try { EvictionDiagnostics.EmitDebug(_componentName, "get_or_add", "miss_build"); } catch { } }

            // Phase 3: re-check and insert
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    try { EvictionMetrics.IncrementHit(_metrics); } catch { }
                    return existing.Value.Value;
                }

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
            var swCleanup = Stopwatch.StartNew();

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (_verbose) { try { EvictionDiagnostics.EmitDebug(_componentName, "cleanup_idle", "begin"); } catch { } }
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
                    try { EvictionMetrics.IncrementEviction(_metrics, EvictionMetrics.Fields.Time); } catch { }
                }
            }

            // Fire callbacks outside the lock
            foreach (var e in toEvict)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);
            swCleanup.Stop();
            try { EvictionMetrics.OnCleanupCompleted(_metrics, swCleanup.Elapsed.TotalMilliseconds); } catch { }
            try
            {
                EvictionDiagnostics.Emit(_componentName, "cleanup_idle", "info", "done",
                    null,
                    new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase){
                        { "evicted", toEvict.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        { "ms", swCleanup.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) }
                    });
            }
            catch { }
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

            try
            {
                EvictionDiagnostics.Emit(_componentName, "dispose", "info", "disposing_cache",
                    null,
                    new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase) { { "evicted_items", allEntries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            }
            catch { }

            foreach (var e in allEntries)
                await _onEvictedAsync(e.Key, e.Value).ConfigureAwait(false);

            // clean up the internal AsyncLock
            _lock.Dispose();

        }
    }
}
