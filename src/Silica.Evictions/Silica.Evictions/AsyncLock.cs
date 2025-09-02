using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Silica.Evictions
{
    public sealed class AsyncLock : IDisposable
    {
        // sync‐guard for lock state and queue
        private readonly object _sync = new();

        // is the lock currently held?
        private bool _isLocked;

        // monotonic ticket counter
        private long _nextTicket;

        // FIFO queue of pending waiters
        private readonly Queue<Waiter> _queue = new();

        // single reusable releaser
        private readonly Releaser _releaser;

        public AsyncLock()
        {
            _releaser = new Releaser(this);
        }

        /// <summary>
        /// Acquire the lock.  If uncontended, returns immediately.
        /// Otherwise you are queued in FIFO order and traced end-to-end.
        /// </summary>
        public Task<IDisposable> LockAsync()
        {
            lock (_sync)
            {
                if (!_isLocked)
                {
                    // fast‐path: immediately grab the lock
                    _isLocked = true;
                    return Task.FromResult<IDisposable>(_releaser);
                }

                // contention: assign a ticket
                var ticket = Interlocked.Increment(ref _nextTicket);

                // enqueue and return the waiter Task
                var tcs = new TaskCompletionSource<IDisposable>(
                              TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue(new Waiter(ticket, tcs));
                return tcs.Task;
            }
        }

        /// <summary>
        /// Internal release logic: hand the lock to the next waiter, if any.
        /// </summary>
        private void Release()
        {
            Waiter next = null;
            lock (_sync)
            {
                if (_queue.Count > 0)
                    next = _queue.Dequeue();
                else
                    _isLocked = false;
            }

            if (next != null)
            {
                // complete the waiter, giving them the lock
                next.Tcs.TrySetResult(_releaser);
            }
        }

        public void Dispose()
        {
            // no unmanaged resources
        }

        /// <summary>
        /// The IDisposable returned by LockAsync().
        /// Calling Dispose() on it releases the lock.
        /// </summary>
        private sealed class Releaser : IDisposable
        {
            private readonly AsyncLock _owner;
            public Releaser(AsyncLock owner) => _owner = owner;

            public void Dispose()
            {
                // trace the unlock itself
                _owner.Release();
            }
        }

        /// <summary>
        /// Holds the ticket number and the TCS for each pending waiter.
        /// </summary>
        private sealed class Waiter
        {
            public long Ticket { get; }
            public TaskCompletionSource<IDisposable> Tcs { get; }

            public Waiter(long ticket, TaskCompletionSource<IDisposable> tcs)
            {
                Ticket = ticket;
                Tcs = tcs;
            }
        }
    }
}
