using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Concurrency.Metrics;
using Silica.Concurrency.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;


namespace Silica.Concurrency
{
    /// <summary>
    /// Result of a lock acquisition: whether it succeeded, plus a strictly
    /// increasing fencing token to accompany all follow-on operations.
    /// </summary>
    [DebuggerDisplay("Granted = {Granted}, Token = {FencingToken}")]
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
    public sealed class LockManager : IDisposable
    {
        // Upper bound for resourceId length to prevent pathological keys and tag blowups.
        // Chosen conservatively; adjust if you have a documented contract elsewhere.
        private const int MaxResourceIdLength = 4096;
        // Upper bound for per-resource fencing token associations to prevent unbounded growth
        // under long-lived contention with high tx churn. Tuned conservatively.
        private const int MaxTokensPerResource = 65536;
        // Static initializer runs once to guarantee exception registration.
        static LockManager()
        {
            try
            {
                ConcurrencyExceptions.RegisterAll();
            }
            catch
            {
                // Never throw from a type initializer; registration is idempotent and safe to ignore on failure.
            }
        }

        // _disposed: 0 == not disposed, 1 == disposed. Use Interlocked.Exchange to set.
        private volatile int _disposed;
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
        // Preserve last-issued token per resource when the full ResourceTokenState is pruned.
        // Keeps monotonicity without retaining the full token map for long-unused resources.
        private readonly ConcurrentDictionary<string, long> _resourceLastIssued
            = new ConcurrentDictionary<string, long>();

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

        private static string ModeToString(LockMode mode)
        {
            // Force invariant, low-cardinality representation for traces/metrics.
            return mode == LockMode.Exclusive ? "exclusive" : "shared";
        }

        public LockManager(
            IMetricsManager metrics,
            ILockRpcClient? rpcClient = null,
            string nodeId = "local",
            string componentName = "Silica.Concurrency",
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
            var comp = string.IsNullOrWhiteSpace(componentName) ? "Silica.Concurrency" : componentName;
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
            if (resourceId.Length > MaxResourceIdLength)
                throw new ArgumentOutOfRangeException(paramName, "resourceId length exceeds maximum allowed.");
        }
        private void ThrowIfDisposed()
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
            {
                throw new LockManagerDisposedException();
            }
        }


        private long GetApproxQueueDepth()
        {
            // If disposed, report 0 deterministically.
            if (System.Threading.Volatile.Read(ref _disposed) != 0) return 0;
            long total = 0;
            // Snapshot to avoid transient issues if Dispose is racing.
            foreach (var kv in _locks)
            {
                try
                {
                    total += kv.Value.GetQueueLength();
                }
                catch
                {
                    // Defensive: ignore transient failures during concurrent shutdown.
                }
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
                "attempt_shared node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", timeout_ms=" + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
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
                // Cancellation is already recorded and traced in the waiter callback (single source of truth).
                throw;
            }
            catch (ReservationOverflowException)
            {
                // Emit outside internal locks to avoid lengthening critical sections.
                TryEmitTrace("AcquireShared", "error",
                    "reservation_overflow node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture),
                    "reservation_overflow",
                    transactionId, resourceId);
                throw;
            }
            catch (ReentrantHoldOverflowException)
            {
                // Emit outside internal locks to avoid lengthening critical sections.
                TryEmitTrace("AcquireShared", "error",
                    "holder_count_overflow node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture),
                    "holder_overflow",
                    transactionId, resourceId);
                throw;
            }

            if (!outcome.Granted)
            {
                // ResourceLock already records wait timeout; avoid double-counting.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Shared);
                TryEmitTrace("AcquireShared", "warn",
                    "acquire_shared_timeout node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", timeout_ms=" + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
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
                "granted_shared node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + token.ToString(CultureInfo.InvariantCulture),
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
                "attempt_exclusive node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", timeout_ms=" + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
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
                // Cancellation is already recorded and traced in the waiter callback (single source of truth).
                throw;
            }
            catch (ReservationOverflowException)
            {
                TryEmitTrace("AcquireExclusive", "error",
                    "reservation_overflow node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture),
                    "reservation_overflow",
                    transactionId, resourceId);
                throw;
            }
            catch (ReentrantHoldOverflowException)
            {
                TryEmitTrace("AcquireExclusive", "error",
                    "holder_count_overflow node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture),
                    "holder_overflow",
                    transactionId, resourceId);
                throw;
            }

            if (!outcome.Granted)
            {
                // ResourceLock already records wait timeout; avoid double-counting.
                ConcurrencyMetrics.RecordAcquireLatency(_metrics, StopwatchUtil.GetElapsedMilliseconds(start), ConcurrencyMetrics.Fields.Exclusive);
                TryEmitTrace("AcquireExclusive", "warn",
                    "acquire_exclusive_timeout node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", timeout_ms=" + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
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
                "granted_exclusive node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + token.ToString(CultureInfo.InvariantCulture),
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

            try
            {
                // Validate token against the per-(resource, txId) association (peek only)
                long expected = 0;
                if (!TryPeekToken(resourceId, transactionId, out expected))
                {
                    // No active token mapping for (resource, tx)
                    // If disposed, treat as best-effort no-op to avoid shutdown fault amplification.
                    if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    {
                        TryEmitTrace("Release", "warn",
                            "post_dispose_missing_token node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture) + ", expected=<none>",
                            "post_dispose_token_missing",
                            transactionId, resourceId,
                            allowWhenDisposed: true);
                        return;
                    }
                    TryEmitTrace("Release", "error",
                        "missing_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture) + ", expected=<none>",
                        "token_missing",
                        transactionId, resourceId);
                    if (_strictReleaseTokens)
                    {
                        throw new InvalidFencingTokenException(resourceId, expected: 0, provided: fencingToken);
                    }
                    // Best-effort mode: treat as no-op release to prevent fault amplification.
                    return;
                }

                string releasedMode = ConcurrencyMetrics.Fields.None;
                bool released = false;
                if (_locks.TryGetValue(resourceId, out var rlock))
                {
                    // Reject mismatched token before attempting release
                    if (expected != fencingToken)
                    {
                        if (System.Threading.Volatile.Read(ref _disposed) != 0)
                        {
                            TryEmitTrace("Release", "warn",
                                "post_dispose_invalid_token node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture) + ", expected=" + expected.ToString(CultureInfo.InvariantCulture),
                                "post_dispose_invalid_token",
                                transactionId, resourceId,
                                allowWhenDisposed: true);
                            return;
                        }
                        TryEmitTrace("Release", "error",
                            "invalid_or_stale_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture) + ", expected=" + expected.ToString(CultureInfo.InvariantCulture),
                            "invalid_token",
                            transactionId, resourceId);
                        if (_strictReleaseTokens)
                        {
                            throw new InvalidFencingTokenException(resourceId, expected: expected, provided: fencingToken);
                        }
                        // Best-effort mode: do not throw; treat as no-op.
                        return;
                    }
                    bool wasHolder = false;
                    released = rlock.ReleaseWithMode(transactionId, out releasedMode, out wasHolder);

                    if (!wasHolder)
                    {
                        // Ensure consistent metrics attribution for misuse/no-op
                        releasedMode = ConcurrencyMetrics.Fields.None;
                        // A valid token was provided, but this transaction was not a holder of the resource.
                        // In strict mode, surface the misuse to callers.
                        TryEmitTrace("Release", _strictReleaseTokens ? "error" : "warn",
                            "release_not_holder node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture),
                            "release_not_holder",
                            transactionId, resourceId);
                        if (_strictReleaseTokens)
                        {
                            throw new ReleaseNotHolderException(resourceId, transactionId);
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
                    if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    {
                        TryEmitTrace("Release", "warn",
                            "post_dispose_release_noop node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + " (no lock found)",
                            "post_dispose_release_noop",
                            transactionId, resourceId,
                            allowWhenDisposed: true);
                        // No-op after dispose — attribute to "none" for metrics clarity
                        releasedMode = ConcurrencyMetrics.Fields.None;
                    }
                    else
                    {
                        TryEmitTrace("Release", "warn",
                            "release_noop node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + " (no lock found)",
                            "release_noop",
                            transactionId, resourceId);
                        // No lock state available — attribute to "none"
                        releasedMode = ConcurrencyMetrics.Fields.None;
                    }
                    // If strict, this inconsistency must surface: token mapping exists but no lock state.
                    if (System.Threading.Volatile.Read(ref _disposed) == 0 && _strictReleaseTokens && expected == fencingToken)
                    {
                        // Emit a more precise trace before throwing to aid root cause.
                        TryEmitTrace("Release", "error",
                            "release_state_inconsistent node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + " (token matched but no lock state)",
                            "release_state_inconsistent",
                            transactionId, resourceId);
                        // The token association exists and matches, but the transaction is not an active holder.
                        // Surface as a not-holder misuse to align with the canonical contract.
                        // Metrics attribution already set to "none" above.
                        throw new ReleaseNotHolderException(resourceId, transactionId);
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

            // Metrics + trace (mode-aware if possible; "none" for no-op/misuse)
            ConcurrencyMetrics.IncrementRelease(_metrics, releasedMode);
            TryEmitTrace("Release", released ? "ok" : "warn",
                "release node=" + _nodeId + ", resource=" + resourceId + ", tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", token=" + fencingToken.ToString(CultureInfo.InvariantCulture) + ", mode=" + releasedMode,
                "release",
                transactionId, resourceId);

                // Note: if not released (e.g., txId wasn’t a holder), the token check still passed,
                // which indicates a release ordering issue worth tracing by upstream callers.
            }
            finally
            {
                // Always clear any edges for this transaction from the wait-for graph.
                // Centralizing here guarantees a single, deterministic removal regardless of early returns.
                _waitForGraph.RemoveTransaction(transactionId);
            }
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
            if (_rpcClient is LocalLockRpcClient)
            {
                // Surface failures as a faulted Task to keep async contract consistent with remote clients.
                try
                {
                    ReleaseLocal(transactionId, resourceId, fencingToken);
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        tcs.SetException(ex);
                    }
                    catch { }
                    return tcs.Task;
                }
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
                "attempt tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId + ", mode=" + ModeToString(mode) + ", timeout_ms=" + timeoutMs.ToString(CultureInfo.InvariantCulture),
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
                        "blocked_by_head_waiter tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", head_tx=" + headWaiterTx.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId + ", mode=" + ModeToString(mode),
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Idempotent set first to block new public acquires
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                // already disposed
                return;
            }

            // Best-effort: drain all locks, cancel queued waiters, and clean tokens for current holders.
            try
            {
                // Snapshot keys once; then shutdown+remove in a single pass
                var lockKeys = new string[_locks.Count];
                {
                    int i = 0;
                    foreach (var kv in _locks)
                    {
                        if (i < lockKeys.Length) { lockKeys[i] = kv.Key; i++; } else { break; }
                    }
                }
                var holderBuffer = new long[32];
                for (int i = 0; i < lockKeys.Length; i++)
                {
                    var key = lockKeys[i];
                    if (key == null) continue;
                    if (_locks.TryGetValue(key, out var rlock))
                    {
                        int holderCount = 0;
                        var holders = rlock.Shutdown(out holderCount, holderBuffer);
                        // Reuse the largest buffer returned so subsequent resources avoid reallocation.
                        if (!object.ReferenceEquals(holders, holderBuffer))
                        {
                            holderBuffer = holders;
                        }
                        for (int h = 0; h < holderCount; h++)
                        {
                            var tx = holders[h];
                            ConsumeToken(key, tx);
                        }
                        CleanupResourceTokensIfEmpty(key);
                    }
                    _locks.TryRemove(key, out _);
                }

                // Wipe all token states deterministically; we already consumed per-holder tokens.
                var tokenKeys = new string[_resourceTokens.Count];
                {
                    int i = 0;
                    foreach (var kv in _resourceTokens)
                    {
                        if (i < tokenKeys.Length) { tokenKeys[i] = kv.Key; i++; } else { break; }
                    }
                }
                for (int i = 0; i < tokenKeys.Length; i++)
                {
                    var rid = tokenKeys[i];
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
            get { return System.Threading.Volatile.Read(ref _disposed) != 0; }
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
            // Record abort event for ops visibility
            ConcurrencyMetrics.IncrementAbort(_metrics);
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
                    "try_acquire_shared_upgrade_conflict tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId,
                    "try_upgrade_conflict",
                    transactionId, resourceId);
                return false;
            }
            catch (ReentrantHoldOverflowException)
            {
                // Try* must not throw; trace and report failure.
                TryEmitTrace("Acquire", "error",
                    "try_acquire_shared_holder_overflow tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId,
                    "try_holder_overflow",
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
                    "try_acquire_exclusive_upgrade_conflict tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId,
                    "try_upgrade_conflict",
                    transactionId, resourceId);
                return false;
            }
            catch (ReentrantHoldOverflowException)
            {
                // Try* must not throw; trace and report failure.
                TryEmitTrace("Acquire", "error",
                    "try_acquire_exclusive_holder_overflow tx=" + transactionId.ToString(CultureInfo.InvariantCulture) + ", resource=" + resourceId,
                    "try_holder_overflow",
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
            // Reserved waiters that have been extracted from the main queue but are not yet granted or canceled.
            // This enables Abort and Shutdown to see and resolve "in flight" reservations deterministically.
            private readonly LinkedList<Waiter> _reserved = new LinkedList<Waiter>();
            // Aborted transactions we must not grant to.
            private readonly HashSet<long> _abortedTx = new HashSet<long>();
            // Prevent unbounded growth if a resource stays “hot” and never goes idle.
            private const int MaxAbortedTxCapacity = 1024;
            // Distinct holders with reference counts per tx
            private readonly Dictionary<long, int> _holders = new Dictionary<long, int>();
            private readonly LinkedList<Waiter> _queue = new LinkedList<Waiter>();
            private const string SharedField = ConcurrencyMetrics.Fields.Shared, ExclusiveField = ConcurrencyMetrics.Fields.Exclusive;
            // Returns true when resource is completely idle (no holders, no queued waiters, and no in-flight reservations).
            private bool IsCompletelyIdleUnsafe()
            {
                // Caller must hold _sync before calling this method.
                return _holders.Count == 0 && _queue.Count == 0 && _reservedCount == 0;
            }
            // ---------- Reserved counter helpers ----------
            private void IncrementReservedChecked()
            {
                // Prevent overflow that would wedge TryAcquireImmediate and upgrades.
                if (_reservedCount == int.MaxValue)
                {
                    throw new ReservationOverflowException(_resourceId, _reservedCount);
                }
                _reservedCount = _reservedCount + 1;
            }

            private void DecrementReservedChecked()
            {
                if (_reservedCount == 0)
                {
                    // Clamp to zero explicitly in case of future edits
                    _reservedCount = 0;
                    return;
                }
                _reservedCount = _reservedCount - 1;
            }

            public ResourceLock(LockManager owner, string resourceId)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                if (resourceId == null) throw new ArgumentNullException(nameof(resourceId));
                if (resourceId.Length == 0) throw new ArgumentException("resourceId must be non-empty.", nameof(resourceId));
                _resourceId = resourceId;
            }
            // Caller holds _sync when invoking this. Prunes _abortedTx if it grows beyond a threshold
            // by removing IDs that have no presence among holders, queued waiters, or reserved waiters.
            private void PruneAbortedIfLargeUnsafe()
            {
                if (_abortedTx.Count <= MaxAbortedTxCapacity) return;
                // Build a small “present” set from holders, queue, and reserved.
                // Use HashSet<long> without LINQ; bounded by current state sizes.
                var present = new HashSet<long>();
                // Holders
                foreach (var kv in _holders)
                {
                    present.Add(kv.Key);
                }
                // Queued waiters
                var node = _queue.First;
                while (node != null)
                {
                    var next = node.Next;
                    var w = node.Value;
                    if (w != null) present.Add(w.TxId);
                    node = next;
                }
                // Reserved waiters
                var rnode = _reserved.First;
                while (rnode != null)
                {
                    var rnext = rnode.Next;
                    var rw = rnode.Value;
                    if (rw != null) present.Add(rw.TxId);
                    rnode = rnext;
                }
                // Now remove any aborted IDs not present anywhere.
                // Snapshot keys to avoid modifying during enumeration.
                long[] abortedKeys = new long[_abortedTx.Count];
                int i = 0;
                foreach (var id in _abortedTx)
                {
                    if (i < abortedKeys.Length) { abortedKeys[i] = id; i++; } else { break; }
                }
                for (int j = 0; j < i; j++)
                {
                    var tx = abortedKeys[j];
                    if (!present.Contains(tx))
                    {
                        _abortedTx.Remove(tx);
                    }
                }
            }

            public IReadOnlyList<long> GetCurrentHolderIds()
            {
                lock (_sync)
                {
                    if (_holders.Count == 0)
                        return Array.Empty<long>();
                    // Copy keys without LINQ into an exactly sized array.
                    // Defensive: snapshot count first to allocate.
                    int count = _holders.Count;
                    var buffer = new long[count];
                    int i = 0;
                    foreach (var kv in _holders)
                    {
                        if (i < buffer.Length)
                        {
                            buffer[i] = kv.Key;
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (i == buffer.Length)
                    {
                        return buffer;
                    }
                    // If we saw fewer elements than initially reported (rare), return trimmed array.
                    var trimmed = new long[i];
                    for (int j = 0; j < i; j++) trimmed[j] = buffer[j];
                    return trimmed;
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
                    // Prevent unbounded growth of aborted markers under churn.
                    PruneAbortedIfLargeUnsafe();
                    // If this transaction was aborted, do not grant.
                    if (_abortedTx.Contains(txId))
                    {
                        return false;
                    }
                    // Do not bypass queued or reserved work; preserve FIFO fairness guarantees.
                    // If any waiter is queued or reserved (handoff window), immediate attempts must not cut the line.
                    if (_reservedCount > 0 || _queue.Count > 0)
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
                    // Prevent unbounded growth of aborted markers under churn.
                    PruneAbortedIfLargeUnsafe();
                    int existingCount = 0;
                    bool alreadyHolder = _holders.TryGetValue(txId, out existingCount) && existingCount > 0;
                    // If this transaction was aborted, do not grant or enqueue.
                    if (_abortedTx.Contains(txId))
                    {
                        // Treat as not granted without enqueue; caller will record outcome.
                        _owner.TryEmitTrace("Acquire", "cancelled",
                            "aborted_tx_rejected_immediate tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(mode) + ", resource=" + _resourceId,
                            "aborted_tx_rejected",
                            txId, resourceId: _resourceId);
                        return new AcquireOutcome(false, false);
                    }
                    if (IsCompatible(mode, txId))
                    {
                        GrantLock(mode, txId);
                        // immediate grant metrics + trace
                        var modeField = mode == LockMode.Exclusive ? ExclusiveField : SharedField;
                        ConcurrencyMetrics.IncrementAcquireImmediate(_owner._metrics, modeField);
                        _owner.TryEmitTrace("Acquire", "ok",
                            "granted_immediate tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(mode) + ", resource=" + _resourceId,
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
                            "upgrade_blocked tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", resource=" + _resourceId + ", reason=not_sole_holder",
                            "upgrade_blocked",
                            txId, resourceId: _resourceId);
                        throw new LockUpgradeIncompatibleException(txId, _resourceId);
                    }

                    // metrics + trace for queued waiter
                    _owner.RecordWaitQueued(mode);
                    _owner.TryEmitTrace("Acquire", "blocked",
                        "queued tx=" + txId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(mode) + ", timeout_ms=" + timeoutMs.ToString(CultureInfo.InvariantCulture) + ", resource=" + _resourceId,
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
                                "cancelled tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId,
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
                                "timeout tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId + ", queue_len_at_enqueue=" + queueLenAtEnqueue.ToString(CultureInfo.InvariantCulture) + ", pos_at_enqueue=" + positionAtEnqueue.ToString(CultureInfo.InvariantCulture) + ", queue_len_now=" + qlenAfter.ToString(CultureInfo.InvariantCulture),
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

            // Returns true if txId was removed; sets modeField to "shared"/"exclusive"/"none" based on state at removal
            public bool ReleaseWithMode(long txId, out string modeField, out bool wasHolder)
            {
                List<Waiter> toGrant = null;
                modeField = ConcurrencyMetrics.Fields.None;
                bool removed = false;
                wasHolder = false;

                lock (_sync)
                {
                    // Prevent unbounded growth of aborted markers under churn.
                    PruneAbortedIfLargeUnsafe();
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
                            // If nothing grantable and we're fully idle, drop aborted marker set to prevent growth.
                            if ((toGrant == null || toGrant.Count == 0) && IsCompletelyIdleUnsafe())
                            {
                                _abortedTx.Clear();
                            }
                        }
                        else
                        {
                            _holders[txId] = heldCount;
                            // Even if other shared holders remain, new shared waiters at the head
                            // may be grantable without waiting for full drain.
                            toGrant = ExtractGrantable();
                        }
                    }
                    else
                    {
                        // No holder for txId. Use "none" attribution and, if we’re otherwise idle here, clear aborted markers too.
                        modeField = ConcurrencyMetrics.Fields.None;
                        if ((toGrant == null || toGrant.Count == 0) && IsCompletelyIdleUnsafe())
                        {
                            _abortedTx.Clear();
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
                        bool abortedAtDecision = false;
                        lock (_sync)
                        {
                            // Prevent unbounded growth during long grant chains.
                            PruneAbortedIfLargeUnsafe();
                            if (w.Granted)
                            {
                                continue;
                            }
                            // If this tx was aborted after reservation, cancel instead of granting.
                            if (_abortedTx.Contains(w.TxId))
                            {
                                // Remove from reserved tracking
                                if (w.ReservedNode != null && w.ReservedNode.List != null)
                                {
                                    _reserved.Remove(w.ReservedNode);
                                    w.ReservedNode = null;
                                }
                                w.Reserved = false;
                                DecrementReservedChecked();
                                // Mark for cancellation outside lock.
                                abortedAtDecision = true;
                            }
                            int c2;
                            bool had2 = _holders.TryGetValue(w.TxId, out c2) && c2 > 0;
                            w.FirstForTx = !had2;
                            // We set Granted here; Reserved was set when extracted from queue.
                            w.Granted = true;
                        }
                        // Phase 2: complete TCS outside lock
                        // If the tx was aborted, cancel instead of granting.
                        if (abortedAtDecision)
                        {
                            try { w.Tcs.TrySetCanceled(); } catch { }
                            _owner.TryEmitTrace("Acquire", "cancelled",
                                "aborted_tx_cancelled_after_reservation tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId,
                                "aborted_tx_cancelled_after_reservation",
                                w.TxId, resourceId: _resourceId);
                            continue;
                        }
                        var grantedOutcome = new AcquireOutcome(true, w.FirstForTx);
                        if (!w.Tcs.TrySetResult(grantedOutcome))
                        {
                            // Completion failed (e.g., late cancellation). Undo reservation and remove from reserved tracking.
                            lock (_sync)
                            {
                                DecrementReservedChecked();
                                if (w.ReservedNode != null && w.ReservedNode.List != null)
                                {
                                    _reserved.Remove(w.ReservedNode);
                                    w.ReservedNode = null;
                                }
                                w.Reserved = false;
                            }
                            continue;
                        }
                        // Phase 3: finalize state under lock
                        lock (_sync)
                        {
                            GrantLock(w.Mode, w.TxId);
                            DecrementReservedChecked();
                            // Remove from reserved tracking on successful finalize
                            if (w.ReservedNode != null && w.ReservedNode.List != null)
                            {
                                _reserved.Remove(w.ReservedNode);
                                w.ReservedNode = null;
                            }
                            w.Reserved = false;
                        }
                        _owner.TryEmitTrace("Acquire", "ok",
                            "granted_after_wait tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId,
                            "granted_after_wait",
                            w.TxId, resourceId: _resourceId);
                    }
                    // After applying these grants, see if more at the head are now grantable
                    lock (_sync)
                    {
                        // Prevent unbounded growth when idle/near-idle after grant.
                        PruneAbortedIfLargeUnsafe();
                        toGrant = ExtractGrantable();
                        // If nothing else is grantable and we’re fully idle now, clear aborted markers.
                        if ((toGrant == null || toGrant.Count == 0) && IsCompletelyIdleUnsafe())
                        {
                            _abortedTx.Clear();
                        }
                    }
                }
                return removed;
            }

            public bool IsUnused
            {
                get
                {
                    // Consider holders, queued waiters, and in-flight reservations.
                    // Caller expects a single atomic snapshot under _sync.
                    lock (_sync)
                    {
                        // A resource is unused only when there are no distinct holders,
                        // no queued waiters, and no reserved (in-flight) waiters.
                        return _holders.Count == 0 && _queue.Count == 0 && _reservedCount == 0;
                    }
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
                List<TaskCompletionSource<AcquireOutcome>> reservedToCancel = null;
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
                    // Cancel all reserved-but-not-granted waiters
                    var rnode = _reserved.First;
                    while (rnode != null)
                    {
                        var rnext = rnode.Next;
                        var rw = rnode.Value;
                        if (!rw.Granted)
                        {
                            if (reservedToCancel == null) reservedToCancel = new List<TaskCompletionSource<AcquireOutcome>>();
                            reservedToCancel.Add(rw.Tcs);
                        }
                        _reserved.Remove(rnode);
                        // Clear reservation flags to keep waiter state consistent.
                        rw.Reserved = false;
                        rw.ReservedNode = null;
                        rnode = rnext;
                    }
                    // Reset reserved count conservatively to zero; we've drained the structures.
                    _reservedCount = 0;
                    // Resource is being shut down; drop aborted markers to prevent growth across lifetimes.
                    _abortedTx.Clear();
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
                        try { toCancel[i].TrySetException(new LockManagerDisposedException()); } catch { }
                    }
                }
                if (reservedToCancel != null)
                {
                    for (int i = 0; i < reservedToCancel.Count; i++)
                    {
                        try { reservedToCancel[i].TrySetException(new LockManagerDisposedException()); } catch { }
                    }
                }
                return holdersSnapshot ?? Array.Empty<long>();
            }

            public int GetQueueLength()
            {
                // Report queued + reserved (in-flight) to reflect true backlog.
                // This preserves “approximate” semantics while improving observability for handoff windows.
                lock (_sync)
                {
                    return _queue.Count + _reservedCount;
                }
            }

            // Returns a snapshot of active transaction ids for this resource (holders, queued waiters, reserved).
            // Deduplicates within the method using a HashSet<long>. No LINQ; caller supplies no buffers to keep API tight.
            internal long[] GetActiveTxIdsSnapshot(out int count)
            {
                // Upper bound is holders + queue + reserved; sizes are small and bounded by live contention.
                // Use a HashSet<long> to deduplicate without LINQ.
                var seen = new HashSet<long>();
                lock (_sync)
                {
                    // Holders
                    foreach (var kv in _holders)
                    {
                        seen.Add(kv.Key);
                    }
                    // Queued waiters
                    var node = _queue.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        var w = node.Value;
                        if (w != null) seen.Add(w.TxId);
                        node = next;
                    }
                    // Reserved waiters
                    var rnode = _reserved.First;
                    while (rnode != null)
                    {
                        var rnext = rnode.Next;
                        var rw = rnode.Value;
                        if (rw != null) seen.Add(rw.TxId);
                        rnode = rnext;
                    }
                }
                // Copy to a compact array
                if (seen.Count == 0)
                {
                    count = 0;
                    return Array.Empty<long>();
                }
                var arr = new long[seen.Count];
                int i = 0;
                foreach (var id in seen)
                {
                    if (i < arr.Length)
                    {
                        arr[i] = id;
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }
                count = i;
                if (i == arr.Length) return arr;
                var trimmed = new long[i];
                for (int j = 0; j < i; j++) trimmed[j] = arr[j];
                return trimmed;
            }

            /// <summary>
            /// Abort a transaction: remove its queued waiters (cancelling them) and release its held lock,
            /// then grant next waiters using a race-safe two-phase handshake.
            /// </summary>
            public void Abort(long txId)
            {
                List<Waiter> toGrant = null;
                List<TaskCompletionSource<AcquireOutcome>> toCancel = null;
                List<Waiter> reservedToCancel = null;

                // Phase A: mutate state under lock, collect actions
                lock (_sync)
                {
                    // Prevent unbounded growth before/after adding a new aborted marker.
                    PruneAbortedIfLargeUnsafe();
                    // Mark as aborted to gate future grants for this tx
                    _abortedTx.Add(txId);
                    // Post-add prune in case we crossed capacity.
                    PruneAbortedIfLargeUnsafe();
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

                    // Also cancel reserved-but-not-granted waiters for this tx
                    var rnode = _reserved.First;
                    while (rnode != null)
                    {
                        var rnext = rnode.Next;
                        var rw = rnode.Value;
                        if (rw.TxId == txId && !rw.Granted)
                        {
                            if (reservedToCancel == null) reservedToCancel = new List<Waiter>();
                            reservedToCancel.Add(rw);
                            // Remove from reserved tracking and counters under lock
                            _reserved.Remove(rnode);
                            DecrementReservedChecked();
                            rw.Reserved = false;
                            rw.ReservedNode = null;
                        }
                        rnode = rnext;
                    }
                    // If no work remains on this resource, clear aborted markers immediately.
                    if ((toGrant == null || toGrant.Count == 0) && IsCompletelyIdleUnsafe())
                    {
                        _abortedTx.Clear();
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

                // Phase B2: cancel reserved waiters outside lock
                if (reservedToCancel != null)
                {
                    for (int i = 0; i < reservedToCancel.Count; i++)
                    {
                        var rw = reservedToCancel[i];
                        try { rw.Tcs.TrySetCanceled(); } catch { }
                        _owner.TryEmitTrace("Acquire", "cancelled",
                            "abort_cancelled_reserved tx=" + rw.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(rw.Mode) + ", resource=" + _resourceId,
                            "abort_cancelled_reserved",
                            rw.TxId, resourceId: _resourceId);
                    }
                }

                // Phase C: perform race-safe grants for extracted waiters
                while (toGrant != null && toGrant.Count > 0)
                {
                    for (int i = 0; i < toGrant.Count; i++)
                    {
                        var w = toGrant[i];
                        // Reserve and compute FirstForTx under lock
                        bool abortedAtDecision = false;
                        lock (_sync)
                        {
                            PruneAbortedIfLargeUnsafe();
                            if (w.Granted)
                            {
                                continue;
                            }
                            // If this tx was marked aborted, cancel instead of granting
                            if (_abortedTx.Contains(w.TxId))
                            {
                                // Undo reservation made by ExtractGrantable
                                DecrementReservedChecked();
                                // Remove from reserved tracking if present
                                if (w.ReservedNode != null && w.ReservedNode.List != null)
                                {
                                    _reserved.Remove(w.ReservedNode);
                                    w.ReservedNode = null;
                                }
                                // Complete cancel outside lock; skip setting Granted.
                                abortedAtDecision = true;
                            }
                            int c3;
                            bool had3 = _holders.TryGetValue(w.TxId, out c3) && c3 > 0;
                            w.FirstForTx = !had3;
                            w.Granted = true;
                        }
                        // Complete outside lock
                        if (abortedAtDecision)
                        {
                            try { w.Tcs.TrySetCanceled(); } catch { }
                            _owner.TryEmitTrace("Acquire", "cancelled",
                                "aborted_tx_cancelled_after_extract tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId,
                                "aborted_tx_cancelled_after_extract",
                                w.TxId, resourceId: _resourceId);
                            continue;
                        }
                        var grantedOutcome = new AcquireOutcome(true, w.FirstForTx);
                        if (!w.Tcs.TrySetResult(grantedOutcome))
                        {
                            // Undo reservation if completion failed
                            lock (_sync) { DecrementReservedChecked(); }
                            continue;
                        }
                        // Finalize state under lock
                        lock (_sync)
                        {
                            GrantLock(w.Mode, w.TxId);
                            DecrementReservedChecked();
                            if (w.ReservedNode != null && w.ReservedNode.List != null)
                            {
                                _reserved.Remove(w.ReservedNode);
                                w.ReservedNode = null;
                            }
                        }
                        _owner.TryEmitTrace("Acquire", "ok",
                            "granted_after_wait tx=" + w.TxId.ToString(CultureInfo.InvariantCulture) + ", mode=" + ModeToString(w.Mode) + ", resource=" + _resourceId,
                            "granted_after_wait",
                            w.TxId, resourceId: _resourceId);
                    }
                    // After applying these grants, see if more at the head are now grantable
                    lock (_sync)
                    {
                        PruneAbortedIfLargeUnsafe();
                        toGrant = ExtractGrantable();
                    }
                    // Caller must hold _sync before calling IsCompletelyIdleUnsafe()
                    lock (_sync)
                    {
                        if ((toGrant == null || toGrant.Count == 0) && IsCompletelyIdleUnsafe())
                        {
                            _abortedTx.Clear();
                        }
                    }
                }
            }


            private List<Waiter> ExtractGrantable()
            {
                var granted = new List<Waiter>();
                if (_queue.Count == 0) return granted;
                // Do not extract/grant when any waiter is already reserved (handoff in progress).
                // This preserves strict FIFO handoff and prevents shared waiters from slipping in.
                if (_reservedCount > 0) return granted;

                var head = _queue.First;
                if (head == null) return granted;

                // Exclusive head: only grant when no holders remain.
                if (head.Value.Mode == LockMode.Exclusive)
                {
                    if (_holders.Count == 0 && _reservedCount == 0)
                    {
                        var w = head.Value;
                        _queue.Remove(head);
                        w.Reserved = true;
                        // Track reservation to block TryAcquireImmediate until finalized
                        IncrementReservedChecked();
                        // Add to reserved list to make reservation visible to Abort/Shutdown
                        w.ReservedNode = _reserved.AddLast(w);
                        granted.Add(w);
                    }
                    return granted;
                }

                // Shared head: grant contiguous shared waiters while compatible.
                var cur = head;
                while (cur != null
                       && cur.Value.Mode == LockMode.Shared
                       && (_holders.Count == 0 || _mode == LockMode.Shared)
                       && _reservedCount == 0)
                {
                    var next = cur.Next;
                    var w = cur.Value;
                    _queue.Remove(cur);
                    w.Reserved = true;
                    // Track reservation for each extracted waiter (shared batch)
                    IncrementReservedChecked();
                    // Add to reserved list to make reservation visible to Abort/Shutdown
                    w.ReservedNode = _reserved.AddLast(w);
                    granted.Add(w);
                    cur = next;
                }
                return granted;
            }

            private bool IsCompatible(LockMode req, long txId)
            {
                // No holders: preserve FIFO fairness — do not bypass queued or reserved work.
                // Allow immediate compatibility only when there is no queue and no reservations.
                if (_holders.Count == 0)
                {
                    return _queue.Count == 0 && _reservedCount == 0;
                }

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
                        throw new ReentrantHoldOverflowException(txId, _resourceId);
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
                // Node in _reserved list when Reserved == true
                public LinkedListNode<Waiter> ReservedNode { get; set; }
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
            if (System.Threading.Volatile.Read(ref _disposed) != 0 && !allowWhenDisposed) return;
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            // Drop low-signal traces unless verbose: attempt/start/blocked can be very chatty under contention.
            // Always keep: error, warn, cancelled, ok, and critical codes like "deadlock".
            // This centralizes volume control without changing call sites.
            try
            {
                bool verbose = Silica.Concurrency.Diagnostics.ConcurrencyDiagnostics.EnableVerbose;
                bool isLowSignal =
                    (string.Equals(status, "attempt", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "start", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase));
                if (!verbose && isLowSignal)
                {
                    // Permit specific critical operations regardless of status if they carry special codes.
                    // Currently only "deadlock" is treated as critical, which is not low-signal anyway.
                    return;
                }
            }
            catch { /* never throw */ }
            // Reentrancy latch: tolerate nested calls but never re-enter on the same thread.
            if (System.Threading.Interlocked.CompareExchange(ref _traceReentrancy, 1, 0) != 0) return;
            try
            {
                string component = (_componentTag.Value as string) ?? "Silica.Concurrency";
                // Slightly larger initial capacity to avoid small rehashes under load
                var more = new Dictionary<string, string>(10, StringComparer.OrdinalIgnoreCase);
                try
                {
                    more["code"] = code ?? string.Empty;
                }
                catch { }
                try
                {
                    more["node"] = _nodeId ?? string.Empty;
                }
                catch { }
                // Preserve semantic status as a tag (low cardinality: attempt/blocked/ok/warn/error/cancelled/start)
                try
                {
                    more["status"] = status ?? string.Empty;
                }
                catch { }
                if (txId.HasValue)
                {
                    try { more["tx_id"] = txId.Value.ToString(CultureInfo.InvariantCulture); } catch { }
                }
                if (!string.IsNullOrEmpty(resourceId))
                {
                    try { more["resource"] = resourceId!; } catch { }
                }
                // Map status -> canonical level for consistent routing
                string level = "info";
                try
                {
                    // Normalize known statuses
                    if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                        level = "error";
                    else if (string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase))
                        level = "warn";
                    else if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                        level = "warn";
                    else if (string.Equals(status, "start", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(status, "attempt", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                        level = "info";
                    else if (string.Equals(status, "debug", StringComparison.OrdinalIgnoreCase))
                        level = "debug";
                    else
                        level = "info";
                }
                catch { level = "info"; }

                ConcurrencyDiagnostics.Emit(component, operation, level, message, null, more);
            }
            catch { /* never throw */ }
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
            var state = _resourceTokens.GetOrAdd(resourceId, _ =>
            {
                // If we previously pruned this resource, resume the LastIssued counter
                // so tokens remain monotonic across prune/recreate cycles.
                var s = new ResourceTokenState();
                if (_resourceLastIssued.TryGetValue(resourceId, out var last) && last > 0)
                {
                    s.LastIssued = last;
                }
                return s;
            });
            lock (state.Sync)
            {
                // Monotonic per resource
                if (state.LastIssued == long.MaxValue)
                {
                    // Emit a trace before throwing so operators can root-cause quickly.
                    TryEmitTrace("FencingToken", "error",
                        "token_overflow resource='" + resourceId + "', last_issued=" + state.LastIssued.ToString(CultureInfo.InvariantCulture),
                        "token_overflow",
                        null, resourceId);
                    throw new FencingTokenOverflowException(resourceId, state.LastIssued);
                }
                // Prune stale token associations if the map grows pathologically large.
                // Keep associations that are currently active on this resource (holders/queued/reserved) and the caller tx.
                // This prevents memory growth under high churn without impacting correctness.
                if (state.TxTokens.Count >= MaxTokensPerResource)
                {
                    try
                    {
                        ResourceLock rlock;
                        long[] active = Array.Empty<long>();
                        int activeCount = 0;
                        if (_locks.TryGetValue(resourceId, out rlock) && rlock != null)
                        {
                            active = rlock.GetActiveTxIdsSnapshot(out activeCount);
                        }
                        // Build a small lookup set for active ids
                        var activeSet = new HashSet<long>();
                        for (int i = 0; i < activeCount; i++)
                        {
                            activeSet.Add(active[i]);
                        }
                        // Snapshot keys to remove outside of the foreach over dictionary
                        long[] keys = new long[state.TxTokens.Count];
                        int k = 0;
                        foreach (var kv in state.TxTokens)
                        {
                            if (k < keys.Length)
                            {
                                keys[k] = kv.Key;
                                k++;
                            }
                            else { break; }
                        }
                        int pruned = 0;
                        for (int i = 0; i < k && state.TxTokens.Count > MaxTokensPerResource / 2; i++)
                        {
                            var id = keys[i];
                            if (id == txId) continue;
                            if (!activeSet.Contains(id))
                            {
                                if (state.TxTokens.Remove(id))
                                {
                                    if (pruned < int.MaxValue) pruned = pruned + 1;
                                }
                            }
                        }
                        // As a last resort, if still above cap, drop arbitrary non-caller entries to reduce pressure.
                        if (state.TxTokens.Count > MaxTokensPerResource)
                        {
                            int target = MaxTokensPerResource;
                            // Snapshot keys again to avoid mutating during enumeration
                            long[] keys2 = new long[state.TxTokens.Count];
                            int k2 = 0;
                            foreach (var kv2 in state.TxTokens)
                            {
                                if (k2 < keys2.Length)
                                {
                                    keys2[k2] = kv2.Key;
                                    k2++;
                                }
                                else { break; }
                            }
                            for (int i = 0; i < k2 && state.TxTokens.Count > target; i++)
                            {
                                var curId = keys2[i];
                                if (curId == txId) continue;
                                if (state.TxTokens.Remove(curId))
                                {
                                    if (pruned < int.MaxValue) pruned = pruned + 1;
                                }
                            }
                        }
                        if (pruned > 0)
                        {
                            // Emit a compact metric for ops visibility; low cardinality.
                            ConcurrencyMetrics.IncrementTokenPruned(_metrics, pruned);
                            TryEmitTrace("FencingToken", "warn",
                                "token_pruned resource='" + resourceId + "', pruned=" + pruned.ToString(CultureInfo.InvariantCulture),
                                "token_pruned",
                                null, resourceId);
                        }
                    }
                    catch
                    {
                        // Never let pruning failures affect correctness of issuance.
                    }
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
                "nonfirst_missing_token node=" + _nodeId + ", resource='" + resourceId + "', tx=" + txId.ToString(CultureInfo.InvariantCulture),
                "nonfirst_missing_token",
                txId, resourceId);
            if (_strictAcquireTokens)
            {
                throw new MissingFencingTokenException(resourceId, txId);
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
                        // Save last-issued counter so we can restore monotonicity if the state
                        // is recreated later, then remove the (now-empty) state to free memory.
                        try
                        {
                            var last = state.LastIssued;
                            if (last > 0)
                            {
                                _resourceLastIssued[resourceId] = last;
                            }
                        }
                        catch { /* best-effort */ }
                        _resourceTokens.TryRemove(resourceId, out _);
                    }
                }
            }
        }
        // Remove all fencing tokens belonging to a transaction across all resources,
        // then prune any empty per-resource token states.
        private void CleanupTokensForTransaction(long txId)
        {
            // Snapshot current resource ids to avoid mutating the collection while enumerating.
            var keys = new string[_resourceTokens.Count];
            {
                int i = 0;
                foreach (var kv in _resourceTokens)
                {
                    if (i < keys.Length) { keys[i] = kv.Key; i++; } else { break; }
                }
            }
            for (int i = 0; i < keys.Length; i++)
            {
                var resourceId = keys[i];
                if (_resourceTokens.TryGetValue(resourceId, out var state))
                {
                    lock (state.Sync)
                    {
                        state.TxTokens.Remove(txId);
                    }
                }
                // Best-effort prune if now empty.
                CleanupResourceTokensIfEmpty(resourceId);
            }
        }

    }
}
