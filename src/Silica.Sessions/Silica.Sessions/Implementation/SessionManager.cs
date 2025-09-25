using System;
using System.Collections.Generic;
using Silica.Sessions.Contracts;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Sessions.Metrics;
using Silica.Sessions.Diagnostics;
using Silica.Exceptions;

namespace Silica.Sessions.Implementation
{
    public sealed class SessionManager : ISessionManager, IDisposable
    {
        private readonly Dictionary<Guid, ISessionContext> _sessions = new();
        private readonly object _lock = new();
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private bool _disposed;
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(_componentName); }

        static SessionManager()
        {
            try { SessionExceptions.RegisterAll(); } catch { /* idempotent */ }
        }

        public SessionManager(IMetricsManager? metrics = null, string componentName = "Silica.Sessions")
        {
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Sessions" : componentName;
            if (_metrics is not null)
            {
                // Observable gauge: report active session count
                SessionMetrics.RegisterAll(_metrics, _componentName, activeSessionsProvider: GetApproxActiveCount);
            }
        }

        private long GetApproxActiveCount()
        {
            lock (_lock)
            {
                long count = 0;
                var e = _sessions.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        var session = e.Current.Value;
                        var state = session.State;
                        // Approximate "active" sessions: exclude Closed/Expired/Idle.
                        if (state == SessionState.Active || state == SessionState.Authenticated || state == SessionState.Created)
                        {
                            count++;
                        }
                    }
                }
                finally { e.Dispose(); }
                return count;
            }
        }

        public ISessionContext CreateSession(
            string? principal,
            TimeSpan idleTimeout,
            TimeSpan transactionTimeout,
            string? coordinatorNode)
        {
            ThrowIfDisposed();
            SessionContext session;
            try
            {
                session = new SessionContext(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    idleTimeout,
                    transactionTimeout,
                    coordinatorNode ?? string.Empty,
                    _metrics,
                    _componentName);
            }
            catch (NegativeTimeoutException ex)
            {
                // Fail-fast with explicit signal when invalid timeouts are provided.
                var tags = new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
                tags["idle.timeout"] = idleTimeout.ToString();
                tags["tx.timeout"] = transactionTimeout.ToString();
                tags["param.name"] = ex.ParamName ?? "timeout";
                SessionDiagnostics.Emit(_componentName, "CreateSession", "error",
                    "invalid_timeout_on_create",
                    ex,
                    tags);
                throw;
            }

            // Defensive: treat null/whitespace principal as unauthenticated (resilient admin-plane behavior).
            if (!string.IsNullOrWhiteSpace(principal))
            {
                try
                {
                    session.Authenticate(principal);
                }
                catch (InvalidPrincipalException)
                {
                    // Resilient admin-plane behavior: proceed unauthenticated and signal.
                    SessionDiagnostics.Emit(_componentName, "CreateSession", "warn",
                        "invalid_principal_on_create",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId, "principal_present", "no"));
                }
            }

            lock (_lock)
            {
                _sessions[session.SessionId] = session;
            }

            var principalPresent = (principal == null || string.IsNullOrWhiteSpace(principal)) ? "no" : "yes";
            SessionMetrics.IncrementCreate(_metrics, principalPresent);
            SessionDiagnostics.Emit(_componentName, "CreateSession", "ok",
                "created",
                null,
                CreateManagerTagMap(session.SessionId, session.GlobalSessionId,
                    "principal_present", principalPresent));
            return session;
        }

        // Convenience ISession getters for frontend adapters (optional).
        public ISession Get(Guid sessionId)
        {
            var s = GetSession(sessionId);
            return (ISession)s;
        }

        public bool TryGet(Guid sessionId, out ISession session)
        {
            if (TryGetSession(sessionId, out var ctx))
            {
                session = (ISession)ctx;
                return true;
            }
            session = default!;
            return false;
        }

        public ISessionContext GetSession(Guid sessionId)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    // Low-noise debug parity for direct get misses.
                    SessionDiagnostics.EmitDebug(_componentName, "GetSession",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    throw new SessionNotFoundException(sessionId);
                }
                return session;
            }
        }

        public bool TryGetSession(Guid sessionId, out ISessionContext session)
        {
            if (_disposed) { session = default!; return false; }
            lock (_lock)
            {
                var found = _sessions.TryGetValue(sessionId, out session);
                if (!found)
                {
                    // Parity with other Try* methods: emit low-noise miss
                    SessionDiagnostics.EmitDebug(_componentName, "TryGetSession",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                }
                return found;
            }
        }

        public void Touch(Guid sessionId)
        {
            ThrowIfDisposed();
            var session = GetSession(sessionId);
            session.Touch();
            SessionDiagnostics.EmitDebug(_componentName, "Touch", "touch",
                CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
        }

        public bool TryTouch(Guid sessionId)
        {
            if (_disposed) return false;
            ISessionContext session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    SessionDiagnostics.EmitDebug(_componentName, "TryTouch",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return false;
                }
            }
            try
            {
                session.Touch();
                SessionDiagnostics.EmitDebug(_componentName, "TryTouch", "touch",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                return true;
            }
            catch (Exception)
            {
                // Defensive: avoid surfacing errors in admin-plane idempotent call.
                SessionDiagnostics.EmitDebug(_componentName, "TryTouch",
                    "skip_unexpected_error_on_touch",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                return false;
            }
        }

        public void SetIdle(Guid sessionId)
        {
            ThrowIfDisposed();
            var session = GetSession(sessionId);
            if (session is ISessionContextInternal impl)
            {
                var prevState = session.State;
                impl.MarkIdle();
                // Emit based on actual state change to Idle
                if (session.State == SessionState.Idle && prevState != SessionState.Idle)
                {
                    SessionMetrics.IncrementIdleSet(_metrics);
                    SessionDiagnostics.Emit(_componentName, "SetIdle", "ok",
                        "idle",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
                else
                {
                    SessionDiagnostics.EmitDebug(_componentName, "SetIdle",
                        "idle_not_set",
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
            }
        }

        public bool TrySetIdle(Guid sessionId)
        {
            if (_disposed) return false;
            ISessionContext session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    SessionDiagnostics.EmitDebug(_componentName, "TrySetIdle",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return false;
                }
            }
            if (session is ISessionContextInternal impl)
            {
                var prevState = session.State;
                try
                {
                    impl.MarkIdle();
                }
                catch (Exception)
                {
                    SessionDiagnostics.EmitDebug(_componentName, "TrySetIdle",
                        "skip_unexpected_error_on_idle",
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                    return false;
                }

                if (session.State == SessionState.Idle && prevState != SessionState.Idle)
                {
                    SessionMetrics.IncrementIdleSet(_metrics);
                    SessionDiagnostics.Emit(_componentName, "TrySetIdle", "ok",
                        "idle",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
                else
                {
                    SessionDiagnostics.EmitDebug(_componentName, "TrySetIdle",
                        "idle_not_set",
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
                return true;
            }
            return false;
        }

        public void Close(Guid sessionId)
        {
            ThrowIfDisposed();
            ISessionContext session;
            // Acquire session reference and remove from dictionary quickly to minimize lock hold.
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    // Observability: document no-op close
                    SessionDiagnostics.EmitDebug(_componentName, "Close",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return;
                }
                _sessions.Remove(sessionId);
            }
            // Deterministic teardown: abort any active transaction before closing.
            try
            {
                if (session.TransactionState == Contracts.TransactionState.Active)
                {
                    // End as Aborted; metrics and diagnostics are handled in EndTransaction.
                    session.EndTransaction(Contracts.TransactionState.Aborted);
                    SessionDiagnostics.Emit(_componentName, "Close", "warn",
                        "tx_aborted_on_close",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
            }
            catch (SessionLifecycleInvalidException)
            {
                // Session concurrently transitioned; proceed with close.
                SessionDiagnostics.EmitDebug(_componentName, "Close",
                    "skip_lifecycle_invalid_on_close",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch (Exception)
            {
                // Defensive: do not block close on unexpected teardown issues.
                SessionDiagnostics.EmitDebug(_componentName, "Close",
                    "skip_unexpected_error_on_close",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }

            if (session is ISessionContextInternal impl)
            {
                impl.MarkClosed();
            }
            SessionMetrics.IncrementClose(_metrics);
            SessionDiagnostics.Emit(_componentName, "Close", "ok",
                "closed",
                null,
                CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
        }

        public bool TryClose(Guid sessionId)
        {
            if (_disposed) return false;
            ISessionContext session;
            // Acquire session reference and remove from dictionary quickly to minimize lock hold.
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    // Observability: document no-op close
                    SessionDiagnostics.EmitDebug(_componentName, "TryClose",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return false;
                }
                _sessions.Remove(sessionId);
            }
            // Deterministic teardown: abort any active transaction before closing.
            try
            {
                if (session.TransactionState == Contracts.TransactionState.Active)
                {
                    session.EndTransaction(Contracts.TransactionState.Aborted);
                    SessionDiagnostics.Emit(_componentName, "TryClose", "warn",
                        "tx_aborted_on_close",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
            }
            catch (SessionLifecycleInvalidException)
            {
                SessionDiagnostics.EmitDebug(_componentName, "TryClose",
                    "skip_lifecycle_invalid_on_close",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch (Exception)
            {
                SessionDiagnostics.EmitDebug(_componentName, "TryClose",
                    "skip_unexpected_error_on_close",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }

            if (session is ISessionContextInternal impl)
            {
                impl.MarkClosed();
            }
            SessionMetrics.IncrementClose(_metrics);
            SessionDiagnostics.Emit(_componentName, "TryClose", "ok",
                "closed",
                null,
                CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            return true;
        }

        public void Expire(Guid sessionId)
        {
            ThrowIfDisposed();
            ISessionContext session;
            // Acquire session reference and remove from dictionary quickly to minimize lock hold.
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    // Observability: document no-op expire
                    SessionDiagnostics.EmitDebug(_componentName, "Expire",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return;
                }
                _sessions.Remove(sessionId);
            }
            // Deterministic teardown: abort any active transaction before expiring.
            try
            {
                if (session.TransactionState == Contracts.TransactionState.Active)
                {
                    session.EndTransaction(Contracts.TransactionState.Aborted);
                    SessionDiagnostics.Emit(_componentName, "Expire", "warn",
                        "tx_aborted_on_expire",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
            }
            catch (SessionLifecycleInvalidException)
            {
                // Session concurrently transitioned; proceed with expire.
                SessionDiagnostics.EmitDebug(_componentName, "Expire",
                    "skip_lifecycle_invalid_on_expire",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch (Exception)
            {
                // Defensive: do not block expire on unexpected teardown issues.
                SessionDiagnostics.EmitDebug(_componentName, "Expire",
                    "skip_unexpected_error_on_expire",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }

            if (session is ISessionContextInternal impl)
            {
                impl.MarkExpired();
            }
            SessionMetrics.IncrementExpire(_metrics);
            // Emit canonical exception for manual/policy expiration (non-idle)
            try
            {
                var ex = new SessionExpiredException(session.SessionId);
                SessionDiagnostics.Emit(_componentName, "Expire", "warn",
                    "expired",
                    ex,
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch
            {
                SessionDiagnostics.Emit(_componentName, "Expire", "warn",
                    "expired",
                    null,
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
        }

        public bool TryExpire(Guid sessionId)
        {
            if (_disposed) return false;
            ISessionContext session;
            // Acquire session reference and remove from dictionary quickly to minimize lock hold.
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    // Observability: document no-op expire
                    SessionDiagnostics.EmitDebug(_componentName, "TryExpire",
                        "session_not_found",
                        CreateManagerTagMap(sessionId, Guid.Empty));
                    return false;
                }
                _sessions.Remove(sessionId);
            }
            // Deterministic teardown: abort any active transaction before expiring.
            try
            {
                if (session.TransactionState == Contracts.TransactionState.Active)
                {
                    session.EndTransaction(Contracts.TransactionState.Aborted);
                    SessionDiagnostics.Emit(_componentName, "TryExpire", "warn",
                        "tx_aborted_on_expire",
                        null,
                        CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
                }
            }
            catch (SessionLifecycleInvalidException)
            {
                SessionDiagnostics.EmitDebug(_componentName, "TryExpire",
                    "skip_lifecycle_invalid_on_expire",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch (Exception)
            {
                SessionDiagnostics.EmitDebug(_componentName, "TryExpire",
                    "skip_unexpected_error_on_expire",
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }

            if (session is ISessionContextInternal impl)
            {
                impl.MarkExpired();
            }
            SessionMetrics.IncrementExpire(_metrics);
            // Emit canonical exception for manual/policy expiration (non-idle)
            try
            {
                var ex = new SessionExpiredException(session.SessionId);
                SessionDiagnostics.Emit(_componentName, "TryExpire", "warn",
                    "expired",
                    ex,
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            catch
            {
                SessionDiagnostics.Emit(_componentName, "TryExpire", "warn",
                    "expired",
                    null,
                    CreateManagerTagMap(session.SessionId, session.GlobalSessionId));
            }
            return true;
        }

        public int EvictIdleSessions(DateTime utcNow)
        {
            ThrowIfDisposed();
            // Defensive: normalize to UTC to avoid mis-sweeps if callers pass local time.
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                utcNow = utcNow.ToUniversalTime();
            }
            // Collect and remove candidates under lock for parity with Close/Expire:
            // ensures expired sessions are not visible to callers during teardown.
            var toExpire = new List<ISessionContext>();
            var keysToRemove = new List<Guid>();
            int evictedCount = 0;
            lock (_lock)
            {
                var e = _sessions.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        var kvp = e.Current;
                        var s = kvp.Value;
                        // Zero idle timeout disables eviction
                        if (s.IdleTimeout == TimeSpan.Zero)
                        {
                            continue;
                        }
                        bool expired = false;
                        var impl = s as ISessionContextInternal;
                        if (impl != null)
                        {
                            // Prefer monotonic timeout check
                            expired = impl.IsIdleExpiredUtc(utcNow);
                        }
                        else
                        {
                            // Fallback to wall-clock
                            var idleCutoff = s.LastActivityUtc.Add(s.IdleTimeout);
                            expired = utcNow >= idleCutoff;
                        }
                        if (expired)
                        {
                            // First pass: collect candidates; do not mutate the dictionary during enumeration.
                            keysToRemove.Add(kvp.Key);
                            toExpire.Add(s);
                            evictedCount++;
                        }
                    }
                }
                finally { e.Dispose(); }

                // Second pass: remove candidates from the dictionary after enumeration completes.
                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _sessions.Remove(keysToRemove[i]);
                }
            }
            // Perform per-session work outside the manager lock.
            for (int i = 0; i < toExpire.Count; i++)
            {
                var s = toExpire[i];
                try
                {
                    if (s.TransactionState == Contracts.TransactionState.Active)
                    {
                        s.EndTransaction(Contracts.TransactionState.Aborted);
                        SessionDiagnostics.Emit(_componentName, "EvictIdleSessions", "warn",
                            "tx_aborted_on_idle_eviction",
                            null,
                            CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                    }
                }
                catch (SessionLifecycleInvalidException)
                {
                    // Session concurrently transitioned; proceed with eviction.
                    SessionDiagnostics.EmitDebug(_componentName, "EvictIdleSessions",
                        "skip_lifecycle_invalid_on_idle_eviction",
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
                catch (Exception)
                {
                    // Defensive: do not let one failure halt the sweep.
                    SessionDiagnostics.EmitDebug(_componentName, "EvictIdleSessions",
                        "skip_unexpected_error_on_idle_eviction",
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }

                if (s is ISessionContextInternal impl)
                {
                    impl.MarkExpired();
                }
                // Observability parity: count expirations caused by idle eviction
                SessionMetrics.IncrementExpire(_metrics);
                // Per-session canonical emission for parity with Expire(Guid)
                try
                {
                    var ex = new IdleTimeoutExpiredException(s.SessionId, s.IdleTimeout);
                    SessionDiagnostics.Emit(_componentName, "EvictIdleSessions", "warn",
                        "expired_on_idle_eviction",
                        ex,
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
                catch
                {
                    SessionDiagnostics.Emit(_componentName, "EvictIdleSessions", "warn",
                        "expired_on_idle_eviction",
                        null,
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
            }
            // Batch metric and canonical batch emission
            SessionMetrics.IncrementEvict(_metrics, evictedCount);
            if (evictedCount > 0)
            {
                // Emit canonical exception once per batch for signal; individual ids are removed
                try
                {
                    var ex = new IdleTimeoutExpiredException(Guid.Empty, TimeSpan.Zero);
                    SessionDiagnostics.Emit(_componentName, "EvictIdleSessions", "warn",
                        "evicted",
                        ex,
                        CreateBatchTagMap(evictedCount));
                }
                catch
                {
                    SessionDiagnostics.Emit(_componentName, "EvictIdleSessions", "warn",
                        "evicted",
                        null,
                        CreateBatchTagMap(evictedCount));
                }
            }
            return evictedCount;
        }

        // NEW: enforce transaction timeout deterministically; abort timed-out transactions.
        public int AbortTimedOutTransactions(DateTime utcNow)
        {
            ThrowIfDisposed();
            // Defensive: normalize to UTC to keep policy consistent with TransactionStartUtc.
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                utcNow = utcNow.ToUniversalTime();
            }
            int aborted = 0;
            // Collect candidates under lock
            var toAbort = new List<ISessionContext>();
            lock (_lock)
            {
                var e = _sessions.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        var s = e.Current.Value;
                        // Only consider sessions with an active transaction.
                        if (s.TransactionState == Contracts.TransactionState.Active && s.TransactionStartUtc.HasValue)
                        {
                            // Zero transaction timeout disables abort
                            if (s.TransactionTimeout == TimeSpan.Zero)
                            {
                                continue;
                            }
                            bool timedOut = false;
                            var impl = s as ISessionContextInternal;
                            if (impl != null)
                            {
                                // Prefer monotonic timeout check
                                timedOut = impl.IsTransactionTimedOutUtc(utcNow);
                            }
                            else
                            {
                                // Fallback to wall-clock
                                var start = s.TransactionStartUtc.Value;
                                var cutoff = start.Add(s.TransactionTimeout);
                                timedOut = utcNow >= cutoff;
                            }
                            if (timedOut)
                            {
                                toAbort.Add(s);
                            }
                        }
                    }
                }
                finally { e.Dispose(); }
            }
            // Abort timed-out transactions outside the manager lock
            for (int i = 0; i < toAbort.Count; i++)
            {
                var s = toAbort[i];
                try
                {
                    // Capture tx id before ending for diagnostic clarity
                    long? txId = s.CurrentTransactionId;
                    s.EndTransaction(Contracts.TransactionState.Aborted);
                    aborted++;
                    SessionMetrics.IncrementTxTimeoutAbort(_metrics);
                    // Build once, augment with tx id if present
                    var tags = new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
                    tags["session.id"] = s.SessionId.ToString();
                    tags["global.session.id"] = s.GlobalSessionId.ToString();
                    if (txId.HasValue)
                    {
                        tags["tx"] = txId.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    SessionDiagnostics.Emit(_componentName, "AbortTimedOutTransactions", "warn",
                        "tx_timeout_aborted",
                        null,
                        tags);
                }
                catch (SessionLifecycleInvalidException)
                {
                    // Session concurrently closed/expired; skip and continue
                    SessionDiagnostics.EmitDebug(_componentName, "AbortTimedOutTransactions",
                        "skip_lifecycle_invalid",
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
                catch (Exception)
                {
                    // Defensive: do not let one failure halt the sweep
                    SessionDiagnostics.EmitDebug(_componentName, "AbortTimedOutTransactions",
                        "skip_unexpected_error",
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
            }
            return aborted;
        }

        // Local helpers: manager-level tag maps
        private static IReadOnlyDictionary<string, string> CreateManagerTagMap(Guid sessionId, Guid globalSessionId)
        {
            var d = new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase);
            d["session.id"] = sessionId.ToString();
            d["global.session.id"] = globalSessionId.ToString();
            return d;
        }
        private static IReadOnlyDictionary<string, string> CreateManagerTagMap(Guid sessionId, Guid globalSessionId, string k1, string v1)
        {
            var d = new Dictionary<string, string>(3, StringComparer.OrdinalIgnoreCase);
            d["session.id"] = sessionId.ToString();
            d["global.session.id"] = globalSessionId.ToString();
            d[k1] = v1;
            return d;
        }
        private static IReadOnlyDictionary<string, string> CreateBatchTagMap(int count)
        {
            var d = new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase);
            d["batch.count"] = count.ToString();
            return d;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Deterministic shutdown: close all sessions safely
            List<ISessionContext> toClose = new List<ISessionContext>();
            lock (_lock)
            {
                var e = _sessions.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        toClose.Add(e.Current.Value);
                    }
                }
                finally { e.Dispose(); }
                _sessions.Clear();
            }
            for (int i = 0; i < toClose.Count; i++)
            {
                var s = toClose[i];
                try
                {
                    if (s.TransactionState == Contracts.TransactionState.Active)
                    {
                        s.EndTransaction(Contracts.TransactionState.Aborted);
                        SessionDiagnostics.Emit(_componentName, "Dispose", "warn",
                            "tx_aborted_on_dispose",
                            null,
                            CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                    }
                }
                catch (Exception)
                {
                    SessionDiagnostics.EmitDebug(_componentName, "Dispose",
                        "skip_unexpected_error_on_dispose",
                        CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
                }
                if (s is ISessionContextInternal impl)
                {
                    impl.MarkClosed();
                }
                SessionMetrics.IncrementClose(_metrics);
                SessionDiagnostics.Emit(_componentName, "Dispose", "ok",
                    "closed_on_dispose",
                    null,
                    CreateManagerTagMap(s.SessionId, s.GlobalSessionId));
            }
        }
    }
}
