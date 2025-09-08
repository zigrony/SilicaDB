using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Concurrency.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    internal readonly struct AcquireOutcome
    {
        public readonly bool Granted;
        public readonly bool FirstForTx;
        public AcquireOutcome(bool granted, bool firstForTx)
        {
            Granted = granted;
            FirstForTx = firstForTx;
        }
    }

    /// <summary>
    /// Manages async, FIFO-fair shared/exclusive locks per resource,
    /// hooks into the wait-for graph, and records wait times.
    /// </summary>
    public class LockManager : IDisposable
    {
        private volatile bool _disposed;
        private readonly WaitForGraph _waitForGraph = new WaitForGraph();
        private readonly ConcurrentDictionary<string, ResourceLock> _locks = new ConcurrentDictionary<string, ResourceLock>();
        // Per-resource fencing token state: monotonic counter + tx->token map
        private sealed class ResourceTokenState
        {
            public long LastIssued;
            public readonly object Sync = new object();
            public readonly Dictionary<long, long> TxTokens = new Dictionary<long, long>();
        }
        private readonly ConcurrentDictionary<string, ResourceTokenState> _resourceTokens
            = new ConcurrentDictionary<string, ResourceTokenState>();

        private readonly ILockRpcClient _rpcClient;
        // Identifier for “this node” (forward-hook for future DTC/RPC)
        // You can wire this up via DI or config; default to “local”
        private readonly string _nodeId;

        // Release behavior: strict (throw on invalid/stale token) vs best-effort (trace + no-op).
        private readonly bool _strictReleaseTokens;

        // Acquire behavior: strict (throw when a non-first hold is missing its token association) vs best-effort (trace + re-issue).
        private readonly bool _strictAcquireTokens;

        // DiagnosticsCore
        private readonly IMetricsManager _metrics;
        private readonly KeyValuePair<string, object> _componentTag;
        [ThreadStatic]
        private static int _traceReentrancy;

        private static class StopwatchUtil
        {
            public static long GetTimestamp() => Stopwatch.GetTimestamp();
            public static double GetElapsedMilliseconds(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        public LockManager(
            IMetricsManager metrics,
            ILockRpcClient? rpcClient = null,
            string nodeId = "local",
            string componentName = "Concurrency",
            bool strictReleaseTokens = true,
            bool strictAcquireTokens = true)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            // Normalize/validate identity inputs
            if (nodeId is null) throw new ArgumentNullException(nameof(nodeId));
            // Treat whitespace as invalid to prevent low-quality tags and RPC boundary confusion
            if (nodeId.Length == 0 || string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("nodeId must be non-empty and non-whitespace.", nameof(nodeId));
            _nodeId = nodeId;
            _rpcClient = rpcClient ?? new LocalLockRpcClient(this);
            var comp = string.IsNullOrWhiteSpace(componentName) ? "Concurrency" : componentName;
            _componentTag = new KeyValuePair<string, object>(TagKeys.Component, comp);
            ConcurrencyMetrics.RegisterAll(_metrics, comp, queueDepthProvider: GetApproxQueueDepth);
            _strictReleaseTokens = strictReleaseTokens;
            _strictAcquireTokens = strictAcquireTokens;
        }

        private static void ValidateResourceId(string resourceId, string paramName)
        {
            if (resourceId is null) throw new ArgumentNullException(paramName);
            if (resourceId.Length == 0 || string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("resourceId must be non-empty and non-whitespace.", paramName);
        }
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("LockManager");
            }
        }


        private long GetApproxQueueDepth()
        {
            if (_disposed) return 0;
            long total = 0;
            foreach (var kv in _locks)
            {
                total += kv.Value.GetQueueLength();
            }
            return total;
        }

        // Replaced AcquireSharedAsync – now delegates over your RPC client:
        public Task<LockGrant> AcquireSharedAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            // Allow Timeout.Infinite (-1) for infinite waits
            if (timeoutMilliseconds < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "timeoutMilliseconds must be >= -1.");
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
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            if (timeoutMilliseconds < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "timeoutMilliseconds must be >= -1.");
            ConcurrencyMetrics.IncrementAcquireAttempt(_metrics, ConcurrencyMetrics.Fields.Shared);
            TryEmitTrace("AcquireShared", "start",
                $"attempt_shared node={_nodeId}, resource={resourceId}, tx={transactionId}, timeout_ms={timeoutMilliseconds}",
                "acquire_start",
                transactionId, resourceId);

            // 1) do the actual lock acquire
            var start = StopwatchUtil.GetTimestamp();
            AcquireOutcome outcome;
            try
            {
                outcome = await AcquireLockInternalAsync(
                    transactionId,
                    resourceId,
                    LockMode.Shared,
                    timeoutMilliseconds,
                    cancellationToken)
                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Record latency for cancellation and emit terminal trace, then rethrow.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Shared);
                TryEmitTrace("AcquireShared", "cancelled",
                    $"acquire_shared_cancelled node={_nodeId}, resource={resourceId}, tx={transactionId}",
                    "acquire_cancelled",
                    transactionId, resourceId);
                throw;
            }

            if (!outcome.Granted)
            {
                // ResourceLock already records wait timeout; avoid double-counting.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Shared);
                TryEmitTrace("AcquireShared", "warn",
                    $"acquire_shared_timeout node={_nodeId}, resource={resourceId}, tx={transactionId}, timeout_ms={timeoutMilliseconds}",
                    "acquire_timeout",
                    transactionId, resourceId);
                return new LockGrant(false, fencingToken: 0);
            }

            // 2) token: issue only on first hold for this tx; otherwise reuse existing
            var token = GetOrIssueToken(resourceId, transactionId, outcome.FirstForTx);

            // 3) metrics + trace
            ConcurrencyMetrics.IncrementAcquire(_metrics, ConcurrencyMetrics.Fields.Shared);
            ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Shared);
            TryEmitTrace("AcquireShared", "ok",
                $"granted_shared node={_nodeId}, resource={resourceId}, tx={transactionId}, token={token}",
                "acquire_granted",
                transactionId, resourceId);

            return new LockGrant(true, token);
        }

        // in LockManager.cs, below AcquireSharedLocalAsync
        internal async Task<LockGrant> AcquireExclusiveLocalAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            if (timeoutMilliseconds < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "timeoutMilliseconds must be >= -1.");
            ConcurrencyMetrics.IncrementAcquireAttempt(_metrics, ConcurrencyMetrics.Fields.Exclusive);
            TryEmitTrace("AcquireExclusive", "start",
                $"attempt_exclusive node={_nodeId}, resource={resourceId}, tx={transactionId}, timeout_ms={timeoutMilliseconds}",
                "acquire_start",
                transactionId, resourceId);

            // 1) do the actual lock acquire
            var start = StopwatchUtil.GetTimestamp();
            AcquireOutcome outcome;
            try
            {
                outcome = await AcquireLockInternalAsync(
                    transactionId,
                    resourceId,
                    LockMode.Exclusive,
                    timeoutMilliseconds,
                    cancellationToken)
                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Record latency for cancellation and emit terminal trace, then rethrow.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Exclusive);
                TryEmitTrace("AcquireExclusive", "cancelled",
                    $"acquire_exclusive_cancelled node={_nodeId}, resource={resourceId}, tx={transactionId}",
                    "acquire_cancelled",
                    transactionId, resourceId);
                throw;
            }

            if (!outcome.Granted)
            {
                // ResourceLock already records wait timeout; avoid double-counting.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Exclusive);
                TryEmitTrace("AcquireExclusive", "warn",
                    $"acquire_exclusive_timeout node={_nodeId}, resource={resourceId}, tx={transactionId}, timeout_ms={timeoutMilliseconds}",
                    "acquire_timeout",
                    transactionId, resourceId);
                return new LockGrant(false, fencingToken: 0);
            }

            // 2) token: issue only on first hold for this tx; otherwise reuse existing
            var token = GetOrIssueToken(resourceId, transactionId, outcome.FirstForTx);

            // 3) metrics + trace
            ConcurrencyMetrics.IncrementAcquire(_metrics, ConcurrencyMetrics.Fields.Exclusive);
            ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Exclusive);
            TryEmitTrace("AcquireExclusive", "ok",
                $"granted_exclusive node={_nodeId}, resource={resourceId}, tx={transactionId}, token={token}",
                "acquire_granted",
                transactionId, resourceId);

            return new LockGrant(true, token);
        }

        /// <summary>
        /// Authoritative local release path. Validates fencing tokens, updates lock state,
        /// prunes token mappings, and emits metrics/traces. Remains valid after disposal.
        /// </summary>
        internal void ReleaseLocal(
            long transactionId,
            string resourceId,
            long fencingToken)
        {
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            // Validate token against the per-(resource, txId) association (peek only)
            long expected = 0;
            if (!TryPeekToken(resourceId, transactionId, out expected))
            {
                // No active token mapping for (resource, tx)
                // If disposed, treat as best-effort no-op to avoid shutdown fault amplification.
                if (_disposed)
                {
                    TryEmitTrace("Release", "warn",
                        "post_dispose_missing_token node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString() + ", token=" + fencingToken.ToString() + ", expected=<none>",
                        "post_dispose_token_missing",
                        transactionId, resourceId,
                        allowWhenDisposed: true);
                    return;
                }
                TryEmitTrace("Release", "error",
                    "missing_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + transactionId.ToString() + ", token=" + fencingToken.ToString() + ", expected=<none>",
                    "token_missing",
                    transactionId, resourceId);
                if (_strictReleaseTokens)
                {
                    throw new InvalidOperationException(
                        "Invalid or stale fencing token for resource '" + resourceId + "'. Expected=<none>, got " + fencingToken.ToString() + ".");
                }
                // Best-effort mode: treat as no-op release to prevent fault amplification.
                return;
            }

            string releasedMode = "unknown";
            bool released = false;
            if (_locks.TryGetValue(resourceId, out var rlock))
            {
                // Reject mismatched token before attempting release
                if (expected != fencingToken)
                {
                    if (_disposed)
                    {
                        TryEmitTrace("Release", "warn",
                            "post_dispose_invalid_token node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString() + ", token=" + fencingToken.ToString() + ", expected=" + expected.ToString(),
                            "post_dispose_invalid_token",
                            transactionId, resourceId,
                            allowWhenDisposed: true);
                        return;
                    }
                    TryEmitTrace("Release", "error",
                        "invalid_or_stale_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + transactionId.ToString() + ", token=" + fencingToken.ToString() + ", expected=" + expected.ToString(),
                        "invalid_token",
                        transactionId, resourceId);
                    if (_strictReleaseTokens)
                    {
                        throw new InvalidOperationException(
                            "Invalid or stale fencing token for resource '" + resourceId + "'. Expected " + expected.ToString() + ", got " + fencingToken.ToString() + ".");
                    }
                    // Best-effort mode: do not throw; treat as no-op.
                    return;
                }
                bool wasHolder = false;
                released = rlock.ReleaseWithMode(transactionId, out releasedMode, out wasHolder);
                _waitForGraph.RemoveTransaction(transactionId);

                if (!wasHolder)
                {
                    // A valid token was provided, but this transaction was not a holder of the resource.
                    // In strict mode, surface the misuse to callers.
                    TryEmitTrace("Release", _strictReleaseTokens ? "error" : "warn",
                        "release_not_holder node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString() + ", token=" + fencingToken.ToString(),
                        "release_not_holder",
                        transactionId, resourceId);
                    if (_strictReleaseTokens)
                    {
                        throw new InvalidOperationException(
                            "Release called with a valid fencing token, but the transaction is not a holder of resource '" + resourceId + "'.");
                    }
                }

                if (rlock.IsUnused)
                {
                    _locks.TryRemove(resourceId, out _);
                    // If no tokens remain for this resource, remove state to avoid growth
                    CleanupResourceTokensIfEmpty(resourceId);
                }
            }
            else
            {
                if (_disposed)
                {
                    TryEmitTrace("Release", "warn",
                        $"post_dispose_release_noop node={_nodeId}, resource={resourceId}, tx={transactionId} (no lock found)",
                        "post_dispose_release_noop",
                        transactionId, resourceId,
                        allowWhenDisposed: true);
                }
                else
                {
                    TryEmitTrace("Release", "warn",
                        $"release_noop node={_nodeId}, resource={resourceId}, tx={transactionId} (no lock found)",
                        "release_noop",
                        transactionId, resourceId);
                }
                // If strict, this inconsistency must surface: token mapping exists but no lock state.
                if (!_disposed && _strictReleaseTokens && expected == fencingToken)
                {
                    throw new InvalidOperationException(
                        "Release called with a valid fencing token but no in-memory lock state exists for resource '" + resourceId + "'.");
                }
                // In lenient mode, prune stale token mappings to avoid leaks when no lock exists.
                if (!_strictReleaseTokens && expected == fencingToken)
                {
                    ConsumeToken(resourceId, transactionId);
                    CleanupResourceTokensIfEmpty(resourceId);
                }
            }

            // Consume token only if we actually removed the holder (last reference for this tx on this resource).
            if (released)
            {
                ConsumeToken(resourceId, transactionId);
                CleanupResourceTokensIfEmpty(resourceId);
            }

            // Metrics + trace (mode-aware if possible)
            ConcurrencyMetrics.IncrementRelease(_metrics, releasedMode);
            TryEmitTrace("Release", released ? "ok" : "warn",
                $"release node={_nodeId}, resource={resourceId}, tx={transactionId}, token={fencingToken}, mode={releasedMode}",
                "release",
                transactionId, resourceId);

            // Note: if not released (e.g., txId wasn’t a holder), the token check still passed,
            // which indicates a release ordering issue worth tracing by upstream callers.
        }

        // Public RPC-aligned release API (mirrors Acquire*Async delegation)
        public Task ReleaseAsync(
            long transactionId,
            string resourceId,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            // Release should not be cancelled to avoid leaks. Ignore caller's CT.
            // If the client is local, bypass RPC to preserve strict-mode exception semantics.
            var local = _rpcClient as LocalLockRpcClient;
            if (local != null)
            {
                ReleaseLocal(transactionId, resourceId, fencingToken);
                return Task.CompletedTask;
            }
            return _rpcClient.ReleaseAsync(_nodeId, transactionId, resourceId, fencingToken, CancellationToken.None);
        }

        public Task<LockGrant> AcquireExclusiveAsync(
            long transactionId,
            string resourceId,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));
            // Allow Timeout.Infinite (-1) for infinite waits, to match shared semantics
            if (timeoutMilliseconds < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "timeoutMilliseconds must be >= -1.");
            return _rpcClient.RequestExclusiveAsync(
                _nodeId,
                transactionId,
                resourceId,
                timeoutMilliseconds,
                cancellationToken);
        }

        private async Task<AcquireOutcome> AcquireLockInternalAsync(
            long txId,
            string resourceId,
            LockMode mode,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rlock = _locks.GetOrAdd(resourceId, _ => new ResourceLock(this, resourceId));

            // Trace the attempt with current holders snapshot
            TryEmitTrace("Acquire", "attempt",
                $"attempt tx={txId}, resource={resourceId}, mode={mode}, timeout_ms={timeoutMs}",
                "acquire_attempt",
                txId, resourceId);

            // Snapshot holders for deadlock graph
            var holders = rlock.GetCurrentHolderIds();
            for (int i = 0; i < holders.Count; i++)
            {
                var h = holders[i];
                // Avoid self-edge; reentrant holds should not create cycles
                if (h == txId) continue;
                _waitForGraph.AddEdge(txId, h);
            }

            // FIFO fairness means a queued head waiter also blocks new entrants,
            // even if current holders would otherwise be compatible. Represent this
            // in the wait-for graph so deadlocks mediated by queued waiters are detectable.
            long headWaiterTx = 0;
            if (rlock.TryGetHeadWaiterTx(out headWaiterTx))
            {
                if (headWaiterTx != 0 && headWaiterTx != txId)
                {
                    _waitForGraph.AddEdge(txId, headWaiterTx);
                    TryEmitTrace("Acquire", "blocked",
                        $"blocked_by_head_waiter tx={txId}, head_tx={headWaiterTx}, resource={resourceId}, mode={mode}",
                        "blocked_by_head_waiter",
                        txId, resourceId);
                }
            }

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

        public bool DetectDeadlocks()
        {
            ThrowIfDisposed();
            var deadlocked = _waitForGraph.DetectCycle();
            if (deadlocked)
            {
                ConcurrencyMetrics.IncrementDeadlock(_metrics);
                // Record graph size for context
                var size = _waitForGraph.CountNodes();
                ConcurrencyMetrics.RecordDeadlockGraphSize(_metrics, size);
                TryEmitTrace("DetectDeadlocks", "error", $"deadlock_detected graph_size={size}", "deadlock");
            }
            return deadlocked;
        }

        public void Dispose()
        {
            // Idempotent set first to block new public acquires
            _disposed = true;

            // Best-effort: drain all locks, cancel queued waiters, and clean tokens for current holders.
            try
            {
                var holderBuffer = new long[32];
                foreach (var kv in _locks)
                {
                    var key = kv.Key;
                    var rlock = kv.Value;
                    int holderCount = 0;
                    // Shutdown returns a snapshot of distinct holder txIds
                    var holders = rlock.Shutdown(out holderCount, holderBuffer);
                    // Consume tokens for holders of this resource
                    for (int i = 0; i < holderCount; i++)
                    {
                        var tx = holders[i];
                        ConsumeToken(key, tx);
                    }
                    CleanupResourceTokensIfEmpty(key);
                }
                // After shutdown, aggressively detach all remaining locks to avoid retention.
                // Safe with ConcurrentDictionary: removal during enumeration is supported.
                foreach (var kv in _locks)
                {
                    _locks.TryRemove(kv.Key, out _);
                }
                // Wipe all token states deterministically; we already consumed per-holder tokens.
                foreach (var kv in _resourceTokens)
                {
                    var rid = kv.Key;
                    // Try remove without locking outer state to avoid holding locks while mutating dictionary.
                    _resourceTokens.TryRemove(rid, out _);
                }
                // Clear the wait-for graph to free memory deterministically
                _waitForGraph.Clear();
            }
            catch
            {
                // never throw from Dispose
            }
        }

        /// <summary>
        /// Indicates whether this LockManager has been disposed. Public read-only.
        /// </summary>
        public bool IsDisposed
        {
            get { return _disposed; }
        }

        /// <summary>
        /// Forcefully removes a transaction from all locks and queues, cancelling its waits
        /// and releasing held locks. Follow-on grants proceed normally.
        /// </summary>
        public void AbortTransaction(long transactionId)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            // Remove from wait-for graph early
            _waitForGraph.RemoveTransaction(transactionId);

            // Scan all resources. ConcurrentDictionary allows safe enumeration.
            foreach (var kv in _locks)
            {
                var rlock = kv.Value;
                rlock.Abort(transactionId);
            }

            // Proactively prune unused locks to prevent dictionary growth after abort.
            // Safe with ConcurrentDictionary enumeration: we probe and remove by key afterward.
            foreach (var kv in _locks)
            {
                var key = kv.Key;
                var rlock = kv.Value;
                if (rlock.IsUnused)
                {
                    _locks.TryRemove(key, out _);
                }
            }

            // Clean up fencing tokens for this transaction across all resources.
            CleanupTokensForTransaction(transactionId);
        }

        // -----------------------------
        // Immediate, no-queue acquire APIs
        // -----------------------------
        // These return immediately without queueing; useful for optimistic probes.
        public bool TryAcquireShared(long transactionId, string resourceId, out long fencingToken)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));

            fencingToken = 0;
            var rlock = _locks.GetOrAdd(resourceId, _ => new ResourceLock(this, resourceId));
            bool granted;
            bool firstForTx;
            try
            {
                granted = rlock.TryAcquireImmediate(transactionId, LockMode.Shared, out firstForTx);
            }
            catch (LockUpgradeIncompatibleException)
            {
                // Try* must not throw; trace and report failure.
                TryEmitTrace("Acquire", "error",
                    "try_acquire_shared_upgrade_conflict tx=" + transactionId.ToString() + ", resource=" + resourceId,
                    "try_upgrade_conflict",
                    transactionId, resourceId);
                return false;
            }
            if (!granted) return false;
            fencingToken = GetOrIssueToken(resourceId, transactionId, firstForTx);
            ConcurrencyMetrics.IncrementAcquire(_metrics, ConcurrencyMetrics.Fields.Shared);
            ConcurrencyMetrics.IncrementAcquireImmediate(_metrics, ConcurrencyMetrics.Fields.Shared);
            TryEmitTrace("Acquire", "ok",
                $"granted_immediate_try tx={transactionId}, mode={LockMode.Shared}, resource={resourceId}, token={fencingToken}",
                "granted_immediate_try",
                transactionId, resourceId);
            return true;
        }

        public bool TryAcquireExclusive(long transactionId, string resourceId, out long fencingToken)
        {
            ThrowIfDisposed();
            if (transactionId <= 0) throw new ArgumentOutOfRangeException(nameof(transactionId), "transactionId must be > 0.");
            ValidateResourceId(resourceId, nameof(resourceId));

            fencingToken = 0;
            var rlock = _locks.GetOrAdd(resourceId, _ => new ResourceLock(this, resourceId));
            bool granted;
            bool firstForTx;
            try
            {
                granted = rlock.TryAcquireImmediate(transactionId, LockMode.Exclusive, out firstForTx);
            }
            catch (LockUpgradeIncompatibleException)
            {
                // Try* must not throw; trace and report failure.
                TryEmitTrace("Acquire", "error",
                    "try_acquire_exclusive_upgrade_conflict tx=" + transactionId.ToString() + ", resource=" + resourceId,
                    "try_upgrade_conflict",
                    transactionId, resourceId);
                return false;
            }
            if (!granted) return false;
            fencingToken = GetOrIssueToken(resourceId, transactionId, firstForTx);
            ConcurrencyMetrics.IncrementAcquire(_metrics, ConcurrencyMetrics.Fields.Exclusive);
            ConcurrencyMetrics.IncrementAcquireImmediate(_metrics, ConcurrencyMetrics.Fields.Exclusive);
            TryEmitTrace("Acquire", "ok",
                $"granted_immediate_try tx={transactionId}, mode={LockMode.Exclusive}, resource={resourceId}, token={fencingToken}",
                "granted_immediate_try",
                transactionId, resourceId);
            return true;
        }

        private class ResourceLock
        {
            private readonly LockManager _owner;
            private readonly string _resourceId;
            private readonly object _sync = new object();
            private LockMode _mode = LockMode.Shared;
            // Count of waiters that have been reserved (extracted from queue) but not yet finalized as holders.
            // This prevents immediate acquires from bypassing queued/reserved waiters during the handoff window.
            private int _reservedCount = 0;
            // Distinct holders with reference counts per tx
            private readonly Dictionary<long, int> _holders = new Dictionary<long, int>();
            private readonly LinkedList<Waiter> _queue = new LinkedList<Waiter>();
            private const string SharedField = ConcurrencyMetrics.Fields.Shared, ExclusiveField = ConcurrencyMetrics.Fields.Exclusive;

            public ResourceLock(LockManager owner, string resourceId)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                if (resourceId == null) throw new ArgumentNullException(nameof(resourceId));
                if (resourceId.Length == 0) throw new ArgumentException("resourceId must be non-empty.", nameof(resourceId));
                _resourceId = resourceId;
            }

            public IReadOnlyList<long> GetCurrentHolderIds()
            {
                lock (_sync)
                {
                    if (_holders.Count == 0)
                        return Array.Empty<long>();
                    // Copy keys without LINQ
                    var arr = new long[_holders.Count];
                    int i = 0;
                    foreach (var kv in _holders)
                    {
                        if (i < arr.Length) { arr[i] = kv.Key; i++; }
                        else { break; }
                    }
                    return arr;
                }
            }

            // Expose current head waiter (if any) for deadlock graph fidelity.
            public bool TryGetHeadWaiterTx(out long headTx)
            {
                headTx = 0;
                lock (_sync)
                {
                    var head = _queue.First;
                    if (head == null) return false;
                    headTx = head.Value != null ? head.Value.TxId : 0;
                    return headTx != 0;
                }
            }

            // Fast-path, no-queue attempt. Returns granted + whether this is the first hold for the tx.
            public bool TryAcquireImmediate(long txId, LockMode mode, out bool firstForTx)
            {
                firstForTx = false;
                lock (_sync)
                {
                    // Do not bypass queued/reserved work; preserve FIFO fairness guarantees.
                    if (_reservedCount > 0)
                    {
                        return false;
                    }
                    int existingCount = 0;
                    bool alreadyHolder = _holders.TryGetValue(txId, out existingCount) && existingCount > 0;
                    if (!IsCompatible(mode, txId))
                    {
                        // Upgrade deadlock check mirrors AcquireAsync’s behavior for exclusives
                        if (mode == LockMode.Exclusive && WouldDeadlockOnUpgrade(txId))
                        {
                            // Surface the same exception semantics as the enqueue path.
                            throw new LockUpgradeIncompatibleException(txId, _resourceId);
                        }
                        return false;
                    }
                    // Compatible now; grant.
                    GrantLock(mode, txId);
                    firstForTx = !alreadyHolder;
                    return true;
                }
            }

            // ────────────────────────────────────────────────────────────────────────────
            // File: Concurrency/LockManager.cs
            // Class:  LockManager.ResourceLock
            // Method: AcquireAsync
            // ────────────────────────────────────────────────────────────────────────────
            public async Task<AcquireOutcome> AcquireAsync(
                long txId,
                LockMode mode,
                int timeoutMs,
                CancellationToken cancellationToken)
            {
                // 0) Fast‐fail if already cancelled
                cancellationToken.ThrowIfCancellationRequested();

                // 1) Enqueue or grant immediately under lock
                TaskCompletionSource<AcquireOutcome> tcs = null;
                Waiter w = null;
                int positionAtEnqueue = -1;
                int queueLenAtEnqueue = -1;

                lock (_sync)
                {
                    int existingCount = 0;
                    bool alreadyHolder = _holders.TryGetValue(txId, out existingCount) && existingCount > 0;
                    if (IsCompatible(mode, txId))
                    {
                        GrantLock(mode, txId);
                        // immediate grant metrics + trace
                        var modeField = mode == LockMode.Exclusive ? ExclusiveField : SharedField;
                        ConcurrencyMetrics.IncrementAcquireImmediate(_owner._metrics, modeField);
                        _owner.TryEmitTrace("Acquire", "ok",
                            $"granted_immediate tx={txId}, mode={mode}, resource={_resourceId}",
                            "granted_immediate",
                            txId, resourceId: _resourceId);
                        // FirstForTx only when we were not a holder before this grant
                        return new AcquireOutcome(true, !alreadyHolder);
                    }

                    // Detect unsafe upgrade that would self-deadlock: requester already holds shared,
                    // and is not the sole holder, but requests exclusive.
                    if (mode == LockMode.Exclusive && WouldDeadlockOnUpgrade(txId))
                    {
                        _owner.TryEmitTrace("Acquire", "error",
                            $"upgrade_blocked tx={txId}, resource={_resourceId}, reason=not_sole_holder",
                            "upgrade_blocked",
                            txId, resourceId: _resourceId);
                        throw new LockUpgradeIncompatibleException(txId, _resourceId);
                    }

                    // metrics + trace for queued waiter
                    _owner.RecordWaitQueued(mode);
                    _owner.TryEmitTrace("Acquire", "blocked",
                        $"queued tx={txId}, mode={mode}, timeout_ms={timeoutMs}, resource={_resourceId}",
                        "wait_queued",
                        txId, resourceId: _resourceId);

                    // Create TCS only when we truly need to queue
                    tcs = new TaskCompletionSource<AcquireOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                    w = new Waiter(txId, mode, tcs);
                    // Position is current queue count before adding the new waiter
                    positionAtEnqueue = _queue.Count;
                    var node = _queue.AddLast(w);
                    w.Node = node;
                    queueLenAtEnqueue = _queue.Count;
                }

                // 2) One CTS for external cancel + timeout
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeoutMs != Timeout.Infinite)
                {
                    linkedCts.CancelAfter(timeoutMs);
                }

                // 3) If timeout or cancel fires first, dequeue and signal
                var reg = linkedCts.Token.Register(() =>
                {
                    // If the waiter has already been reserved for grant, do nothing.
                    bool alreadyGranted = false;
                    lock (_sync)
                    {
                        if (w.Granted || w.Reserved)
                        {
                            alreadyGranted = true;
                        }
                        else
                        {
                            if (w.Node.List != null)
                            {
                                _queue.Remove(w.Node);
                            }
                        }
                    }

                    if (alreadyGranted)
                    {
                        // Grant path won; do not cancel or timeout this waiter.
                        return;
                    }

                    // No-op: double-checked removal already handled above.

                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Only record if this registration actually completes the waiter.
                        if (tcs.TrySetCanceled(cancellationToken))
                        {
                            var modeField = w.Mode == LockMode.Exclusive ? ExclusiveField : SharedField;
                            ConcurrencyMetrics.IncrementAcquireCancelled(_owner._metrics, modeField);
                            _owner.TryEmitTrace("Acquire", "cancelled",
                                $"cancelled tx={w.TxId}, mode={w.Mode}, resource={_resourceId}",
                                "wait_cancelled",
                                w.TxId, resourceId: _resourceId);
                        }
                    }
                    else
                    {
                        int qlenAfter;
                        lock (_sync) { qlenAfter = _queue.Count; }
                        // Only record if we actually complete with a timeout (i.e., waiter not already completed).
                        if (tcs.TrySetResult(new AcquireOutcome(false, false)))
                        {
                            _owner.RecordWaitTimeout(w.Mode);
                            _owner.TryEmitTrace("Acquire", "warn",
                                $"timeout tx={w.TxId}, mode={w.Mode}, resource={_resourceId}, queue_len_at_enqueue={queueLenAtEnqueue}, pos_at_enqueue={positionAtEnqueue}, queue_len_now={qlenAfter}",
                                "wait_timeout",
                                w.TxId, resourceId: _resourceId);
                        }
                    }
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
            // Keep a single authoritative release path to avoid divergence.
            public void Release(long txId)
            {
                string modeField;
                bool wasHolder;
                ReleaseWithMode(txId, out modeField, out wasHolder);
            }

            // Returns true if txId was removed; sets modeField to "shared"/"exclusive"/"unknown" based on state at removal
            public bool ReleaseWithMode(long txId, out string modeField, out bool wasHolder)
            {
                List<Waiter> toGrant = null;
                modeField = "unknown";
                bool removed = false;
                wasHolder = false;

                lock (_sync)
                {
                    int heldCount = 0;
                    if (_holders.TryGetValue(txId, out heldCount) && heldCount > 0)
                    {
                        wasHolder = true;
                        // Determine held mode attribution before removal
                        // Attribution by current mode and distinct holder count
                        modeField = (_mode == LockMode.Exclusive && _holders.Count == 1) ? ExclusiveField : SharedField;
                        // Defensive: never underflow; clamp to zero
                        if (heldCount > 0) heldCount = heldCount - 1;
                        if (heldCount == 0)
                        {
                            _holders.Remove(txId);
                            removed = true;
                            if (_holders.Count == 0)
                                _mode = LockMode.Shared;
                            // After removing this holder (possibly last), see who can run next.
                            toGrant = ExtractGrantable();
                        }
                        else
                        {
                            _holders[txId] = heldCount;
                            // Even if other shared holders remain, new shared waiters at the head
                            // may be grantable without waiting for full drain.
                            toGrant = ExtractGrantable();
                        }
                    }
                }

                // Grant loop: chain grant as long as new waiters become eligible
                while (toGrant != null && toGrant.Count > 0)
                {
                    for (int i = 0; i < toGrant.Count; i++)
                    {
                        var w = toGrant[i];
                        // Phase 1: compute FirstForTx and mark Granted under lock
                        lock (_sync)
                        {
                            if (w.Granted)
                            {
                                continue;
                            }
                            int c2;
                            bool had2 = _holders.TryGetValue(w.TxId, out c2) && c2 > 0;
                            w.FirstForTx = !had2;
                            // We set Granted here; Reserved was set when extracted from queue.
                            w.Granted = true;
                        }
                        // Phase 2: complete TCS outside lock
                        var grantedOutcome = new AcquireOutcome(true, w.FirstForTx);
                        if (!w.Tcs.TrySetResult(grantedOutcome))
                        {
                            // Completion failed (e.g., late cancellation). Undo reservation.
                            lock (_sync)
                            {
                                if (_reservedCount > 0) _reservedCount = _reservedCount - 1;
                            }
                            continue;
                        }
                        // Phase 3: finalize state under lock
                        lock (_sync)
                        {
                            GrantLock(w.Mode, w.TxId);
                            if (_reservedCount > 0) _reservedCount = _reservedCount - 1;
                        }
                        _owner.TryEmitTrace("Acquire", "ok",
                            $"granted_after_wait tx={w.TxId}, mode={w.Mode}, resource={_resourceId}",
                            "granted_after_wait",
                            w.TxId, resourceId: _resourceId);
                    }
                    // After applying these grants, see if more at the head are now grantable
                    lock (_sync)
                    {
                        toGrant = ExtractGrantable();
                    }
                }
                return removed;
            }

            public bool IsUnused
            {
                get
                {
                    lock (_sync)
                        return _holders.Count == 0 && _queue.Count == 0;
                }
            }

            /// <summary>
            /// Shutdown this resource: cancel all queued waiters, clear the queue, snapshot holders.
            /// Returns an array (possibly reused) containing distinct holder txIds and sets holderCount.
            /// The caller must treat the returned array as read-only until next call.
            /// </summary>
            public long[] Shutdown(out int holderCount, long[] reuseBuffer)
            {
                List<TaskCompletionSource<AcquireOutcome>> toCancel = null;
                long[] holdersSnapshot = reuseBuffer;
                holderCount = 0;
                lock (_sync)
                {
                    // Cancel all queued waiters
                    var node = _queue.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        var w = node.Value;
                        if (!w.Granted)
                        {
                            if (toCancel == null) toCancel = new List<TaskCompletionSource<AcquireOutcome>>();
                            toCancel.Add(w.Tcs);
                        }
                        _queue.Remove(node);
                        node = next;
                    }
                    // Snapshot distinct holders
                    if (_holders.Count > 0)
                    {
                        if (holdersSnapshot == null || holdersSnapshot.Length < _holders.Count)
                        {
                            holdersSnapshot = new long[_holders.Count];
                        }
                        int i = 0;
                        foreach (var kv in _holders)
                        {
                            if (i < holdersSnapshot.Length)
                            {
                                holdersSnapshot[i] = kv.Key;
                                i++;
                            }
                            else
                            {
                                break;
                            }
                        }
                        holderCount = i;
                    }
                    // Reset mode when emptied
                    if (_holders.Count == 0)
                    {
                        _mode = LockMode.Shared;
                    }
                }
                if (toCancel != null)
                {
                    for (int i = 0; i < toCancel.Count; i++)
                    {
                        try { toCancel[i].TrySetException(new ObjectDisposedException("LockManager")); } catch { }
                    }
                }
                return holdersSnapshot ?? Array.Empty<long>();
            }

            public int GetQueueLength()
            {
                lock (_sync) return _queue.Count;
            }

            /// <summary>
            /// Abort a transaction: remove its queued waiters (cancelling them) and release its held lock,
            /// then grant next waiters using a race-safe two-phase handshake.
            /// </summary>
            public void Abort(long txId)
            {
                List<Waiter> toGrant = null;
                List<TaskCompletionSource<AcquireOutcome>> toCancel = null;

                // Phase A: mutate state under lock, collect actions
                lock (_sync)
                {
                    // Release held lock if present and collect grantable
                    if (_holders.Remove(txId))
                    {
                        if (_holders.Count == 0)
                        {
                            _mode = LockMode.Shared;
                        }
                        toGrant = ExtractGrantable();
                    }

                    // Remove queued waiters for this tx and collect their TCS for cancellation
                    var node = _queue.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        var w = node.Value;
                        if (w.TxId == txId && !w.Granted)
                        {
                            if (toCancel == null) toCancel = new List<TaskCompletionSource<AcquireOutcome>>();
                            toCancel.Add(w.Tcs);
                            _queue.Remove(node);
                        }
                        node = next;
                    }
                }

                // Phase B: cancel queued waiters outside lock
                if (toCancel != null)
                {
                    for (int i = 0; i < toCancel.Count; i++)
                    {
                        var tcs = toCancel[i];
                        tcs.TrySetCanceled();
                    }
                }

                // Phase C: perform race-safe grants for extracted waiters
                while (toGrant != null && toGrant.Count > 0)
                {
                    for (int i = 0; i < toGrant.Count; i++)
                    {
                        var w = toGrant[i];
                        // Reserve and compute FirstForTx under lock
                        lock (_sync)
                        {
                            if (w.Granted)
                            {
                                continue;
                            }
                            int c3;
                            bool had3 = _holders.TryGetValue(w.TxId, out c3) && c3 > 0;
                            w.FirstForTx = !had3;
                            w.Granted = true;
                        }
                        // Complete outside lock
                        var grantedOutcome = new AcquireOutcome(true, w.FirstForTx);
                        if (!w.Tcs.TrySetResult(grantedOutcome))
                        {
                            // Undo reservation if completion failed
                            lock (_sync)
                            {
                                if (_reservedCount > 0) _reservedCount = _reservedCount - 1;
                            }
                            continue;
                        }
                        // Finalize state under lock
                        lock (_sync)
                        {
                            GrantLock(w.Mode, w.TxId);
                            if (_reservedCount > 0) _reservedCount = _reservedCount - 1;
                        }
                        _owner.TryEmitTrace("Acquire", "ok",
                            $"granted_after_wait tx={w.TxId}, mode={w.Mode}, resource={_resourceId}",
                            "granted_after_wait",
                            w.TxId, resourceId: _resourceId);
                    }
                    // After applying these grants, see if more at the head are now grantable
                    lock (_sync)
                    {
                        toGrant = ExtractGrantable();
                    }
                }
            }


            private List<Waiter> ExtractGrantable()
            {
                var granted = new List<Waiter>();
                if (_queue.Count == 0) return granted;

                var head = _queue.First;
                if (head == null) return granted;

                // Exclusive head: only grant when no holders remain.
                if (head.Value.Mode == LockMode.Exclusive)
                {
                    if (_holders.Count == 0)
                    {
                        var w = head.Value;
                        _queue.Remove(head);
                        w.Reserved = true;
                        // Track reservation to block TryAcquireImmediate until finalized
                        _reservedCount = (_reservedCount == int.MaxValue) ? int.MaxValue : _reservedCount + 1;
                        granted.Add(w);
                    }
                    return granted;
                }

                // Shared head: grant contiguous shared waiters while compatible.
                var cur = head;
                while (cur != null
                       && cur.Value.Mode == LockMode.Shared
                       && (_holders.Count == 0 || _mode == LockMode.Shared))
                {
                    var next = cur.Next;
                    var w = cur.Value;
                    _queue.Remove(cur);
                    w.Reserved = true;
                    // Track reservation for each extracted waiter (shared batch)
                    _reservedCount = (_reservedCount == int.MaxValue) ? int.MaxValue : _reservedCount + 1;
                    granted.Add(w);
                    cur = next;
                }
                return granted;
            }

            private bool IsCompatible(LockMode req, long txId)
            {
                if (_holders.Count == 0) return true;

                // Re-entrant cases
                int countForTx = 0;
                if (_holders.TryGetValue(txId, out countForTx) && countForTx > 0)
                {
                    if (req == LockMode.Shared)
                    {
                        // Re-entrant shared is always compatible.
                        // It does not bypass queued work because it doesn't need to.
                        return true;
                    }
                    // Re-entrant upgrade to exclusive:
                    // - must be the sole holder (prevents self-deadlock), and
                    // - must not bypass queued/reserved work to preserve FIFO fairness.
                    if (_holders.Count == 1)
                    {
                        // Do not allow upgrade when either the queue has entries or
                        // there is any pending reserved waiter during handoff.
                        return _queue.Count == 0 && _reservedCount == 0;
                    }
                    return false;
                }

                // Non-reentrant
                // Only allow a new shared holder when current mode is shared and
                // there is no queued work, to preserve FIFO fairness for waiters.
                // Also disallow bypass when any reservations are in progress.
                return req == LockMode.Shared && _mode == LockMode.Shared && _queue.Count == 0 && _reservedCount == 0;
            }

            // Caller must hold _sync. True when the requester holds a shared lock
            // but is not the sole holder, and is asking for exclusive, which would deadlock.
            private bool WouldDeadlockOnUpgrade(long txId)
            {
                // Only consider when the tx is an existing holder.
                int countForTx = 0;
                if (!_holders.TryGetValue(txId, out countForTx) || countForTx == 0) return false;
                // If there are multiple holders, upgrading would block everyone including itself.
                if (_holders.Count > 1) return true;
                // Sole holder is safe for upgrade.
                return false;
            }

            private void GrantLock(LockMode mode, long txId)
            {
                _mode = mode;
                int count;
                if (_holders.TryGetValue(txId, out count))
                {
                    // Guard against integer overflow on reentrant holds
                    if (count == int.MaxValue)
                    {
                        // Trace and fail fast to avoid corrupting reference counts
                        _owner.TryEmitTrace("Acquire", "error",
                            "holder_count_overflow tx=" + txId.ToString() + ", resource=" + _resourceId,
                            "holder_overflow",
                            txId, resourceId: _resourceId);
                        throw new InvalidOperationException("Reentrant hold count overflow for tx " + txId.ToString() + " on resource '" + _resourceId + "'.");
                    }
                    _holders[txId] = count + 1;
                }
                else
                {
                    _holders[txId] = 1;
                }
            }

            private class Waiter
            {
                public long TxId { get; }
                public LockMode Mode { get; }
                public TaskCompletionSource<AcquireOutcome> Tcs { get; }
                public LinkedListNode<Waiter> Node { get; set; }
                // Reservation/handshake fields to avoid cancel/grant races
                public bool Reserved { get; set; }
                public bool Granted { get; set; }
                public bool FirstForTx { get; set; }
                public Waiter(long txId, LockMode mode, TaskCompletionSource<AcquireOutcome> tcs)
                {
                    TxId = txId;
                    Mode = mode;
                    Tcs = tcs;
                }
            }
        }
        // ─────────────────────────────────────────────────────────────
        // Diagnostics helpers (metrics + tracing)
        // ─────────────────────────────────────────────────────────────
        private void RecordWaitQueued(LockMode mode)
        {
            var field = mode == LockMode.Exclusive ? ConcurrencyMetrics.Fields.Exclusive : ConcurrencyMetrics.Fields.Shared;
            ConcurrencyMetrics.IncrementWaitQueued(_metrics, field);
        }

        private void RecordWaitTimeout(LockMode mode)
        {
            var field = mode == LockMode.Exclusive ? ConcurrencyMetrics.Fields.Exclusive : ConcurrencyMetrics.Fields.Shared;
            ConcurrencyMetrics.IncrementWaitTimeout(_metrics, field);
        }

        private void TryEmitTrace(string operation, string status, string message, string code, long? txId = null, string? resourceId = null, bool allowWhenDisposed = false)
        {
            if (_disposed && !allowWhenDisposed) return;
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            // Reentrancy latch: tolerate nested calls but never re-enter on the same thread.
            if (System.Threading.Interlocked.CompareExchange(ref _traceReentrancy, 1, 0) != 0) return;
            try
            {
                string component = (_componentTag.Value as string) ?? "Concurrency";
                var tags = new Dictionary<string, string>(6, StringComparer.OrdinalIgnoreCase)
                {
                    ["code"] = code,
                    ["node"] = _nodeId
                };
                if (txId.HasValue) tags["tx_id"] = txId.Value.ToString();
                if (!string.IsNullOrEmpty(resourceId)) tags["resource"] = resourceId!;
                DiagnosticsCoreBootstrap.Instance.Traces.Emit(
                    component: component,
                    operation: operation,
                    status: status,
                    tags: tags,
                    message: message
                );
            }
            catch
            {
                // never throw
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _traceReentrancy, 0);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Fencing token helpers (per-resource, per-tx)
        // ─────────────────────────────────────────────────────────────
        private long IssueToken(string resourceId, long txId)
        {
            var state = _resourceTokens.GetOrAdd(resourceId, _ => new ResourceTokenState());
            lock (state.Sync)
            {
                // Monotonic per resource
                if (state.LastIssued == long.MaxValue)
                {
                    // Emit a trace before throwing so operators can root-cause quickly.
                    TryEmitTrace("FencingToken", "error",
                        "token_overflow resource='" + resourceId + "', last_issued=" + state.LastIssued.ToString(),
                        "token_overflow",
                        null, resourceId);
                    throw new InvalidOperationException("Fencing token overflow for resource '" + resourceId + "'.");
                }
                state.LastIssued = state.LastIssued + 1;
                var token = state.LastIssued;
                state.TxTokens[txId] = token;
                return token;
            }
        }

        private bool TryPeekToken(string resourceId, long txId, out long expected)
        {
            expected = 0;
            if (!_resourceTokens.TryGetValue(resourceId, out var state))
                return false;
            lock (state.Sync)
            {
                long have;
                if (!state.TxTokens.TryGetValue(txId, out have))
                {
                    return false;
                }
                expected = have;
                return true;
            }
        }
        private void ConsumeToken(string resourceId, long txId)
        {
            if (_resourceTokens.TryGetValue(resourceId, out var state))
            {
                lock (state.Sync)
                {
                    state.TxTokens.Remove(txId);
                }
            }
        }

        private bool TryGetExistingToken(string resourceId, long txId, out long token)
        {
            token = 0;
            if (!_resourceTokens.TryGetValue(resourceId, out var state)) return false;
            lock (state.Sync)
            {
                return state.TxTokens.TryGetValue(txId, out token);
            }
        }

        private long GetOrIssueToken(string resourceId, long txId, bool firstForTx)
        {
            if (firstForTx)
            {
                return IssueToken(resourceId, txId);
            }
            long existing;
            if (TryGetExistingToken(resourceId, txId, out existing))
            {
                return existing;
            }
            // Non-first hold but mapping is missing: this indicates an upstream ordering or state bug.
            // Emit a trace and either throw (strict) or re-issue (best-effort) to avoid breaking clients.
            TryEmitTrace("FencingToken", _strictAcquireTokens ? "error" : "warn",
                "nonfirst_missing_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + txId.ToString(),
                "nonfirst_missing_token",
                txId, resourceId);
            if (_strictAcquireTokens)
            {
                throw new InvalidOperationException(
                    "Missing fencing token mapping for non-first hold on resource '" + resourceId + "', tx " + txId.ToString() + ".");
            }
            // Best-effort: issue a new token to keep the system running.
            return IssueToken(resourceId, txId);
        }


        private void CleanupResourceTokensIfEmpty(string resourceId)
        {
            if (_resourceTokens.TryGetValue(resourceId, out var state))
            {
                lock (state.Sync)
                {
                    if (state.TxTokens.Count == 0)
                    {
                        _resourceTokens.TryRemove(resourceId, out _);
                    }
                }
            }
        }
        // Remove all fencing tokens belonging to a transaction across all resources,
        // then prune any empty per-resource token states.
        private void CleanupTokensForTransaction(long txId)
        {
            foreach (var kv in _resourceTokens)
            {
                var resourceId = kv.Key;
                var state = kv.Value;
                lock (state.Sync)
                {
                    // Remove directly; double-lookup avoided.
                    state.TxTokens.Remove(txId);
                    if (state.TxTokens.Count == 0)
                    {
                        // Attempt pruning outside the lock to avoid holding during dictionary mutation.
                    }
                }
                // Remove empty state if possible (best effort).
                CleanupResourceTokensIfEmpty(resourceId);
            }
        }

    }
}
