using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Evictions.Interfaces;
using Silica.Evictions.Exceptions;

namespace Silica.Evictions
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
            if (cache is null) throw new EvictionNullCacheRegistrationException();

            lock (_sync)
            {
                // avoid double-registration without LINQ
                for (int i = 0; i < _caches.Count; i++)
                {
                    if (ReferenceEquals(_caches[i].InnerCache, cache))
                        throw new EvictionDuplicateCacheRegistrationException();
                }

                _caches.Add(new Wrapper<TKey, TValue>(cache));
            }
        }

        /// <summary>
        /// Stops the manager from ticking this cache.
        /// </summary>
        public void UnregisterCache<TKey, TValue>(IAsyncEvictionCache<TKey, TValue> cache)
        {
            if (cache is null) throw new EvictionNullCacheRegistrationException();

            lock (_sync)
            {
                // remove first match without LINQ
                for (int i = 0; i < _caches.Count; i++)
                {
                    if (ReferenceEquals(_caches[i].InnerCache, cache))
                    {
                        _caches.RemoveAt(i);
                        break;
                    }
                }
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
