using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Evictions.Interfaces;

namespace SilicaDB.Evictions
{
    public sealed class EvictionManager : IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly Timer _timer;
        private readonly object _sync = new();
        private readonly List<IEvictionCacheWrapper> _caches = new();

        // Non-generic facade so we can store heterogeneous caches
        private interface IEvictionCacheWrapper
        {
            ValueTask CleanupIdleAsync();
            object InnerCache { get; }
        }

        // Wraps IAsyncEvictionCache<TKey, TValue>
        private sealed class Wrapper<TKey, TValue> : IEvictionCacheWrapper
        {
            public IAsyncEvictionCache<TKey, TValue> Cache { get; }
            public Wrapper(IAsyncEvictionCache<TKey, TValue> cache) => Cache = cache;
            public ValueTask CleanupIdleAsync() => Cache.CleanupIdleAsync();
            public object InnerCache => Cache!;
        }

        /// <summary>
        /// interval: how often to call CleanupIdleAsync on each registered cache
        /// </summary>
        public EvictionManager(TimeSpan interval)
        {
            _interval = interval;
            _timer = new Timer(_ => _ = OnTickAsync(), null, _interval, _interval);
        }

        /// <summary>
        /// Register a cache so its CleanupIdleAsync is called each tick.
        /// </summary>
        public void RegisterCache<TKey, TValue>(IAsyncEvictionCache<TKey, TValue> cache)
        {
            if (cache is null) throw new ArgumentNullException(nameof(cache));

            lock (_sync)
            {
                // avoid double-registration
                if (_caches.Any(w => ReferenceEquals(w.InnerCache, cache)))
                    return;

                _caches.Add(new Wrapper<TKey, TValue>(cache));
            }
        }

        /// <summary>
        /// Stops the manager from ticking this cache.
        /// </summary>
        public void UnregisterCache<TKey, TValue>(IAsyncEvictionCache<TKey, TValue> cache)
        {
            if (cache is null) throw new ArgumentNullException(nameof(cache));

            lock (_sync)
            {
                var found = _caches
                    .FirstOrDefault(w => ReferenceEquals(w.InnerCache, cache));
                if (found != null)
                    _caches.Remove(found);
            }
        }

        /// <summary>
        /// Called by the timer on each tick.
        /// Snapshots the list and calls CleanupIdleAsync on every cache.
        /// </summary>
        private async Task OnTickAsync()
        {
            IEvictionCacheWrapper[] snapshot;
            lock (_sync)
            {
                snapshot = _caches.ToArray();
            }

            foreach (var wrapper in snapshot)
            {
                try
                {
                    await wrapper.CleanupIdleAsync().ConfigureAwait(false);
                }
                catch
                {
                    // swallow any cache-specific errors
                }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
