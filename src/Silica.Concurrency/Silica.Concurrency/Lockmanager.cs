// LockMode.cs
using Silica.Observability.Tracing;
using Silica.Observability.Instrumentation;
using Silica.Observability.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;


namespace Silica.Concurrency
{
    /// <summary>
    /// Result of a lock acquisition: whether it succeeded, plus a strictly
    /// increasing fencing token to accompany all follow-on operations.
    /// </summary>
    public readonly struct LockGrant
    {
        public bool Granted { get; }
        public long FencingToken { get; }

        public LockGrant(bool granted, long fencingToken)
        {
            Granted = granted;
            FencingToken = fencingToken;
        }
    }

    /// <summary>
    /// Manages async, FIFO-fair shared/exclusive locks per resource,
    /// hooks into the wait-for graph, and records wait times.
    /// </summary>
    public class LockManager : IDisposable
    {
        private readonly LockMetrics _lockMetrics;
        private readonly WaitForGraph _waitForGraph = new WaitForGraph();
        private readonly ConcurrentDictionary<string, ResourceLock> _locks = new ConcurrentDictionary<string, ResourceLock>();
        // Monotonic counter for all fencing tokens issued by this node
        private long _globalFencingCounter = 0;

        // Last fencing token seen per resource
        private readonly ConcurrentDictionary<string, long> _lastFencingToken
            = new ConcurrentDictionary<string, long>();

        private readonly ILockRpcClient _rpcClient;
        // Identifier for “this node” (forward-hook for future DTC/RPC)
        // You can wire this up via DI or config; default to “local”
        private readonly string _nodeId;


        public LockManager(LockMetrics waitMetrics, ILockRpcClient? rpcClient = null, string nodeId = "local")
        {
            _lockMetrics = waitMetrics;
            _nodeId = nodeId;
            _rpcClient = rpcClient ?? new LocalLockRpcClient(this);
        }


        // Replaced AcquireSharedAsync – now delegates over your RPC client:
        public Task<LockGrant> AcquireSharedAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            // delegate to either remote node or the local stub
            return _rpcClient.RequestSharedAsync(
                _nodeId,
                transactionId,
                resourceId,
                timeoutMilliseconds,
                cancellationToken);
        }

        // Your old in-process logic is moved here:
        internal async Task<LockGrant> AcquireSharedLocalAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            // 1) do the actual lock acquire
            var success = await AcquireLockInternalAsync(
                transactionId,
                resourceId,
                LockMode.Shared,
                timeoutMilliseconds,
                cancellationToken)
              .ConfigureAwait(false);

            if (!success)
                return new LockGrant(false, fencingToken: 0);

            // 2) issue and record a new fencing token
            var token = Interlocked.Increment(ref _globalFencingCounter);
            _lastFencingToken[resourceId] = token;

            // 3) emit your existing trace
            Silica.Observability.Tracing.Trace.Info(
                TraceCategory.Locks,
                src: "LockManager.AcquireShared",
                res: $"node={_nodeId}, resource={resourceId}, token={token}");

            return new LockGrant(true, token);
        }

        // in LockManager.cs, below AcquireSharedLocalAsync
        internal async Task<LockGrant> AcquireExclusiveLocalAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            // 1) do the actual lock acquire
            var success = await AcquireLockInternalAsync(
                transactionId,
                resourceId,
                LockMode.Exclusive,
                timeoutMilliseconds,
                cancellationToken)
              .ConfigureAwait(false);

            if (!success)
                return new LockGrant(false, fencingToken: 0);

            // 2) issue and record a new fencing token
            var token = Interlocked.Increment(ref _globalFencingCounter);
            _lastFencingToken[resourceId] = token;

            // 3) emit the same trace you already have
            Silica.Observability.Tracing.Trace.Info(
                TraceCategory.Locks,
                src: "LockManager.AcquireExclusive",
                res: $"node={_nodeId}, resource={resourceId}, token={token}");

            return new LockGrant(true, token);
        }

        /// <summary>
        /// RPC‐stub release: just calls through to the normal ReleaseLock code.
        /// </summary>
        internal void ReleaseLocal(
            long transactionId,
            string resourceId,
            long fencingToken)
        {
            // you already validate token and call rlock.Release inside ReleaseLock
            ReleaseLock(transactionId, resourceId, fencingToken);
        }


        public Task<LockGrant> AcquireExclusiveAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            return _rpcClient.RequestExclusiveAsync(
                _nodeId,
                transactionId,
                resourceId,
                timeoutMilliseconds,
                cancellationToken);
        }

        private async Task<bool> AcquireLockInternalAsync(
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

        public void ReleaseLock(long transactionId, string resourceId, long fencingToken)
        {
            // Validate token
            if (!_lastFencingToken.TryGetValue(resourceId, out var lastToken)
                || fencingToken != lastToken)
            {
                throw new InvalidOperationException(
                    $"Invalid or stale fencing token for resource {resourceId}. " +
                    $"Expected {lastToken}, got {fencingToken}");
            }

            // Trace the valid release
            Silica.Observability.Tracing.Trace.Info(
                TraceCategory.Locks,
                src: "LockManager.ReleaseLock",
                res: $"node={_nodeId}, resource={resourceId}, token={fencingToken}");

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

            // ────────────────────────────────────────────────────────────────────────────
            // File: Concurrency/LockManager.cs
            // Class:  LockManager.ResourceLock
            // Method: AcquireAsync
            // ────────────────────────────────────────────────────────────────────────────
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

                    // record a "blocked" trace event before queueing
                    if (Silica.Observability.Tracing.Trace.IsEnabled(TraceCategory.Locks))
                    {
                        Silica.Observability.Tracing.Trace.Emit(
                            TraceEvent.New(
                                TraceCategory.Locks,
                                Silica.Observability.Tracing.TraceEventType.Blocked,
                                source: "LockManager.Blocked",
                                resource: txId.ToString(),
                                message: $"mode={mode}, timeout={timeoutMs}"
                            )
                        );
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
                    {
                        w.Tcs.TrySetResult(true);

                        // Emit duration-based trace if we have a queued timestamp
                        if (Silica.Observability.Tracing.Trace.IsEnabled(TraceCategory.Locks) && w.QueuedAt.HasValue)
                        {
                            var durMicros = (int)((Stopwatch.GetTimestamp() - w.QueuedAt.Value) * 1_000_000 / Stopwatch.Frequency);
                            Silica.Observability.Tracing.Trace.Emit(
                                TraceEvent.New(
                                    TraceCategory.Locks,
                                    Silica.Observability.Tracing.TraceEventType.Blocked,
                                    source: "LockManager.Granted",
                                    resource: w.TxId.ToString(),
                                    message: $"mode={w.Mode} granted after wait",
                                    durMicros: durMicros
                                )
                            );
                        }
                    }
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
                public long? QueuedAt { get; }

                public Waiter(long txId, LockMode mode, TaskCompletionSource<bool> tcs)
                {
                    TxId = txId;
                    Mode = mode;
                    Tcs = tcs;
                    QueuedAt = Stopwatch.GetTimestamp();
                }
            }
        }
    }
}
