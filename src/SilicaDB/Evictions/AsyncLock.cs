namespace SilicaDB.Evictions
{
    /// <summary>
    /// Simple async‐mutex. Matches the pattern in AsyncStorageDeviceBase.
    /// </summary>
    public sealed class AsyncLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly Task<IDisposable> _releaserTask;
        private readonly IDisposable _releaser;

        public AsyncLock()
        {
            _releaser = new Releaser(this);
            _releaserTask = Task.FromResult(_releaser);
        }

        /// <summary>
        /// Awaitable lock. Dispose the returned token to release.
        /// </summary>
        public Task<IDisposable> LockAsync()
        {
            var wait = _semaphore.WaitAsync();
            if (wait.IsCompleted)
                return _releaserTask;

            return wait.ContinueWith(
                (_, state) => (IDisposable)state!,
                _releaser,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly AsyncLock _toRelease;
            public Releaser(AsyncLock toRelease) => _toRelease = toRelease;
            public void Dispose() => _toRelease._semaphore.Release();
        }

        /// <summary>
        /// Dispose the underlying SemaphoreSlim.
        /// </summary>
        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }

}