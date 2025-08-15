// LockMode.cs
using SilicaDB.Instrumentation;
using SilicaDB.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace SilicaDB.Concurrency
{
    /// <summary>
    /// Manages async, FIFO-fair shared/exclusive locks per resource,
    /// hooks into the wait-for graph, and records wait times.
    /// </summary>
    public class LockManager : IDisposable
    {
        private readonly LockMetrics _lockMetrics;
        private readonly WaitForGraph _waitForGraph = new WaitForGraph();
        private readonly ConcurrentDictionary<string, ResourceLock> _locks
            = new ConcurrentDictionary<string, ResourceLock>();

        public LockManager(LockMetrics waitMetrics)
        {
            _lockMetrics = waitMetrics;
        }

        public Task<bool> AcquireSharedAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            return AcquireLockAsync(transactionId, resourceId, LockMode.Shared, timeoutMilliseconds, cancellationToken);
        }

        public Task<bool> AcquireExclusiveAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            return AcquireLockAsync(transactionId, resourceId, LockMode.Exclusive, timeoutMilliseconds, cancellationToken);
        }

        private async Task<bool> AcquireLockAsync(
            long txId,
            string resourceId,
            LockMode mode,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            using var m = _lockMetrics.StartWaitEvent("Lock_AcquireLockAsync");
            cancellationToken.ThrowIfCancellationRequested();

            var rlock = _locks.GetOrAdd(resourceId, _ => new ResourceLock());

            // Snapshot holders for deadlock graph
            var holders = rlock.GetCurrentHolderIds();
            foreach (var h in holders)
                _waitForGraph.AddEdge(txId, h);

            try
            {
                return await rlock.AcquireAsync(txId, mode, timeoutMs, cancellationToken)
                                    .ConfigureAwait(false);
            }
            finally
            {
                _waitForGraph.RemoveTransaction(txId);
            }
        }

        public void ReleaseLock(long transactionId, string resourceId)
        {
            if (_locks.TryGetValue(resourceId, out var rlock))
            {
                rlock.Release(transactionId);
                _waitForGraph.RemoveTransaction(transactionId);

                if (rlock.IsUnused)
                    _locks.TryRemove(resourceId, out _);
            }
        }

        public bool DetectDeadlocks() => _waitForGraph.DetectCycle();

        public void Dispose() { /* nothing to dispose here */ }

        private class ResourceLock
        {
            private readonly object _sync = new object();
            private LockMode _mode = LockMode.Shared;
            private readonly HashSet<long> _holders = new HashSet<long>();
            private readonly LinkedList<Waiter> _queue = new LinkedList<Waiter>();

            public IReadOnlyList<long> GetCurrentHolderIds()
            {
                lock (_sync)
                    return _holders.ToList();
            }

            public async Task<bool> AcquireAsync(
                long txId,
                LockMode mode,
                int timeoutMs,
                CancellationToken cancellationToken)
            {
                // 0) Fast‐fail if already cancelled
                cancellationToken.ThrowIfCancellationRequested();

                // 1) Enqueue or grant immediately under lock
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiter w;
                LinkedListNode<Waiter> node;

                lock (_sync)
                {
                    if (IsCompatible(mode, txId))
                    {
                        GrantLock(mode, txId);
                        return true;
                    }

                    w = new Waiter(txId, mode, tcs);
                    node = _queue.AddLast(w);
                    w.Node = node;
                }

                // 2) One CTS for external cancel + timeout
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(timeoutMs);

                // 3) If timeout or cancel fires first, dequeue and signal
                var reg = linkedCts.Token.Register(() =>
                {
                    lock (_sync)
                    {
                        if (w.Node.List != null)
                            _queue.Remove(w.Node);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        tcs.TrySetCanceled(cancellationToken);
                    else
                        tcs.TrySetResult(false);
                });

                // 4) Await the TCS; always clean up registration + CTS afterward
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    reg.Dispose();
                    linkedCts.Dispose();
                }
            }

            public void Release(long txId)
            {
                List<Waiter> toGrant = null;

                lock (_sync)
                {
                    if (_holders.Remove(txId))
                    {
                        if (_holders.Count == 0)
                            _mode = LockMode.Shared;
                        toGrant = ExtractGrantable();
                    }
                }

                if (toGrant != null)
                {
                    foreach (var w in toGrant)
                        w.Tcs.TrySetResult(true);
                }
            }

            public bool IsUnused
            {
                get
                {
                    lock (_sync)
                        return _holders.Count == 0 && _queue.Count == 0;
                }
            }

            private List<Waiter> ExtractGrantable()
            {
                var granted = new List<Waiter>();
                if (_queue.Count == 0) return granted;

                var head = _queue.First;

                if (head.Value.Mode == LockMode.Exclusive)
                {
                    if (_holders.Count == 0)
                    {
                        var w = head.Value;
                        _queue.RemoveFirst();
                        GrantLock(w.Mode, w.TxId);
                        granted.Add(w);
                    }
                }
                else
                {
                    var cur = head;
                    while (cur != null
                            && cur.Value.Mode == LockMode.Shared
                            && (_holders.Count == 0 || _mode == LockMode.Shared))
                    {
                        var w = cur.Value;
                        var next = cur.Next;
                        _queue.Remove(cur);
                        GrantLock(w.Mode, w.TxId);
                        granted.Add(w);
                        cur = next;
                    }
                }

                return granted;
            }

            private bool IsCompatible(LockMode req, long txId)
            {
                if (_holders.Count == 0) return true;

                // Re-entrant cases
                if (_holders.Contains(txId))
                {
                    if (req == LockMode.Shared) return true;
                    // Upgrade to exclusive only if we are the sole holder
                    return _holders.Count == 1;
                }

                // Non-reentrant
                return req == LockMode.Shared && _mode == LockMode.Shared && _queue.Count == 0;
            }

            private void GrantLock(LockMode mode, long txId)
            {
                _mode = mode;
                _holders.Add(txId);
            }

            private class Waiter
            {
                public long TxId { get; }
                public LockMode Mode { get; }
                public TaskCompletionSource<bool> Tcs { get; }
                public LinkedListNode<Waiter> Node { get; set; }

                public Waiter(long txId, LockMode mode, TaskCompletionSource<bool> tcs)
                {
                    TxId = txId;
                    Mode = mode;
                    Tcs = tcs;
                }
            }
        }
    }
}
