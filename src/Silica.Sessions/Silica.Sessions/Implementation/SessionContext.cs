using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Silica.Sessions.Contracts;
using Silica.DiagnosticsCore.Metrics;
using Silica.Sessions.Metrics;
using Silica.Sessions.Diagnostics;
using System.Globalization;
using Silica.Exceptions;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Silica.Sessions.Implementation
{
    public sealed class SessionContext : ISessionContext, ISessionContextInternal
    {
        // Operational safety caps for long-lived sessions
        private const int MaxDiagnosticTags = 64;
        private const int MaxParticipantNodes = 64;
        private const int MaxQueryLength = 8192; // characters
        private const int MaxCoordinatorNameLength = 256; // characters
        private const int MaxParticipantNameLength = 256; // characters
        private const int MaxDiagnosticTagValueLength = 2048; // characters
        private const int MaxDiagnosticTagKeyLength = 256; // characters (deterministic cap)
        private const int MaxPrincipalLength = 256; // characters (deterministic cap)

        private readonly Dictionary<string, string> _diagnosticTags;
        private readonly List<string> _participantNodes;
        private string _principal;
        private string _coordinatorNode;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private readonly object _stateLock = new();
        // Monotonic start tick for latency measurement
        private long? _txStartTicks;
        // Monotonic last-activity ticks for deterministic idle eviction
        private long _lastActivityTicks;

        public SessionContext(Guid sessionId, Guid globalSessionId, TimeSpan idleTimeout, TimeSpan transactionTimeout, string? coordinatorNode, IMetricsManager? metrics = null, string componentName = "Silica.Sessions")
        {
            if (idleTimeout < TimeSpan.Zero) throw new NegativeTimeoutException(nameof(idleTimeout), idleTimeout);
            if (transactionTimeout < TimeSpan.Zero) throw new NegativeTimeoutException(nameof(transactionTimeout), transactionTimeout);

            SessionId = sessionId != Guid.Empty ? sessionId : Guid.NewGuid();
            GlobalSessionId = globalSessionId != Guid.Empty ? globalSessionId : Guid.NewGuid();

            // Compute a log‑safe surrogate once at construction
            LogId = ComputeLogId(SessionId);

            State = SessionState.Created;
            CreatedUtc = DateTime.UtcNow;
            LastActivityUtc = CreatedUtc;
            _lastActivityTicks = Stopwatch.GetTimestamp();

            IdleTimeout = idleTimeout;
            TransactionTimeout = transactionTimeout;

            IsolationLevel = IsolationLevel.ReadCommitted;
            _principal = string.Empty;
            // Normalize component name once up front for early diagnostics in ctor.
            var normalizedComponentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Sessions" : componentName;
            // Normalize coordinator node consistently with SetCoordinatorNode (trim first)
            if (coordinatorNode == null)
            {
                _coordinatorNode = string.Empty;
            }
            else
            {
                var trimmed = coordinatorNode.Trim();
                if (trimmed.Length == 0)
                {
                    _coordinatorNode = string.Empty;
                }
                else if (trimmed.Length > MaxCoordinatorNameLength)
                {
                    _coordinatorNode = trimmed.Substring(0, MaxCoordinatorNameLength);
                    SessionDiagnostics.EmitDebug(normalizedComponentName, "SessionContext",
                        "coordinator_truncated_ctor",
                        CreateSessionTagMapWith("original_len", trimmed.Length.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    _coordinatorNode = trimmed;
                }
            }
            _metrics = metrics;
            _componentName = normalizedComponentName;

            _diagnosticTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _participantNodes = new List<string>();

            SessionMetrics.IncrementStateTransition(_metrics, "Created");
            SessionDiagnostics.Emit(_componentName, "SessionContext", "ok",
                "created session_id=" + SessionId.ToString() + ", global_session_id=" + GlobalSessionId.ToString(),
                null,
                null);
        }
        private static string ComputeLogId(Guid id)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id.ToString("N")));
            // 12 hex chars (~48 bits) is plenty for uniqueness in logs
            return Convert.ToHexString(hash).Substring(0, 12);
        }

        // Identity & lifecycle
        public Guid SessionId { get; }
        public Guid GlobalSessionId { get; }
        /// <summary>
        /// A log‑safe surrogate identifier derived from <see cref="SessionId"/>.
        /// This value is stable for the lifetime of the session but cannot be used
        /// to impersonate or resume the session.
        /// </summary>
        public string LogId { get; }
        public string Principal => _principal;
        public SessionState State { get; private set; }
        public DateTime CreatedUtc { get; }
        public DateTime LastActivityUtc { get; private set; }

        // Transaction anchor
        public long? CurrentTransactionId { get; private set; }
        public TransactionState TransactionState { get; private set; } = TransactionState.None;
        public DateTime? TransactionStartUtc { get; private set; }
        public IsolationLevel IsolationLevel { get; private set; }
        public long? SnapshotLsn { get; private set; }

        // Timeouts
        public TimeSpan IdleTimeout { get; }
        public TimeSpan TransactionTimeout { get; }

        // Diagnostics
        public string? CurrentQuery { get; private set; }
        public IReadOnlyDictionary<string, string> DiagnosticTags
        {
            get
            {
                // Snapshot copy under lock to avoid concurrent enumeration races.
                Dictionary<string, string> snap;
                lock (_stateLock)
                {
                    snap = new Dictionary<string, string>(_diagnosticTags.Count, StringComparer.OrdinalIgnoreCase);
                    var e = _diagnosticTags.GetEnumerator();
                    try
                    {
                        while (e.MoveNext())
                        {
                            var kv = e.Current;
                            snap[kv.Key] = kv.Value;
                        }
                    }
                    finally { e.Dispose(); }
                }
                return new ReadOnlyDictionary<string, string>(snap);
            }
        }

        public int DiagnosticTagCount
        {
            get
            {
                lock (_stateLock)
                {
                    return _diagnosticTags.Count;
                }
            }
        }
        // Distributed
        public string CoordinatorNode => _coordinatorNode;
        public IReadOnlyList<string> ParticipantNodes
        {
            get
            {
                // Snapshot copy under lock for stable enumeration.
                List<string> snap;
                lock (_stateLock)
                {
                    snap = new List<string>(_participantNodes.Count);
                    for (int i = 0; i < _participantNodes.Count; i++)
                    {
                        snap.Add(_participantNodes[i]);
                    }
                }
                return new ReadOnlyCollection<string>(snap);
            }
        }
        public int ParticipantCount
        {
            get
            {
                lock (_stateLock)
                {
                    return _participantNodes.Count;
                }
            }
        }

        public long? GlobalTransactionId { get; private set; }

        // Lifecycle ops
        public void Touch()
        {
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                {
                    // Do not mutate closed/expired sessions.
                    return;
                }
                LastActivityUtc = DateTime.UtcNow;
                _lastActivityTicks = Stopwatch.GetTimestamp();
                var previous = State;
                if (previous == SessionState.Created || previous == SessionState.Authenticated || previous == SessionState.Idle)
                {
                    State = SessionState.Active;
                    SessionMetrics.IncrementStateTransition(_metrics, "Active");
                }
            }
            // Emit metrics outside the lock to minimize contention in hot paths.
            SessionMetrics.IncrementTouch(_metrics);
            SessionDiagnostics.EmitDebug(_componentName, "Touch", "touch session_id=" + SessionId.ToString());
        }

        public void Authenticate(string principal)
        {
            if (string.IsNullOrWhiteSpace(principal))
                throw new InvalidPrincipalException();

            // Normalize and cap principal deterministically to avoid audit drift and oversized identity
            string normalized = principal.Trim();
            if (normalized.Length == 0) throw new InvalidPrincipalException();
            if (normalized.Length > MaxPrincipalLength)
            {
                var originalLen = normalized.Length;
                normalized = normalized.Substring(0, MaxPrincipalLength);
                SessionDiagnostics.EmitDebug(_componentName, "Authenticate",
                    "principal_truncated",
                    CreateSessionTagMapWith("original_len", originalLen.ToString(CultureInfo.InvariantCulture)));
            }

            lock (_stateLock)
            {
                // Do not allow mutations on closed/expired sessions.
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "Authenticate");
                // Once authenticated, principal is immutable (enterprise audit stability).
                if (!string.IsNullOrEmpty(_principal))
                    throw new PrincipalChangeNotAllowedException();
                // Do not allow authentication while a transaction is active.
                if (TransactionState == TransactionState.Active)
                    throw new SessionLifecycleInvalidException(State, "Authenticate");
                _principal = normalized;
                if (State != SessionState.Authenticated)
                {
                    State = SessionState.Authenticated;
                    SessionMetrics.IncrementStateTransition(_metrics, "Authenticated");
                }
            }
            SessionDiagnostics.Emit(_componentName, "Authenticate", "ok",
                "auth session_id=" + SessionId.ToString(),
                null,
                CreateSessionTagMapWith("principal.present", "yes"));
            Touch(); // Preserve current behavior: promote to Active on activity
        }

        public void SetIsolationLevel(IsolationLevel level)
        {
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "SetIsolationLevel");
                // Do not allow isolation level changes while a transaction is active.
                // Preserves transactional semantics and avoids mid-flight isolation switches that could violate guarantees.
                if (TransactionState == TransactionState.Active)
                    throw new SessionLifecycleInvalidException(State, "SetIsolationLevel");
                IsolationLevel = level;
            }
            // Emit debug under verbose mode to avoid dual API; traces tolerate verbosity.
            SessionDiagnostics.EmitDebug(_componentName, "SetIsolationLevel",
                "isolation",
                CreateSessionTagMapWith("level", level.ToString()));
        }

        public void BeginTransaction(long txId, long snapshotLsn)
        {
            if (txId <= 0) throw new InvalidTransactionIdException(txId);
            if (snapshotLsn <= 0)
            {
                var ex = new InvalidSnapshotLsnException(snapshotLsn);
                SessionDiagnostics.Emit(_componentName, "BeginTransaction", "error",
                    "invalid snapshot_lsn",
                    ex,
                    CreateSessionTagMapWith("snapshot_lsn", snapshotLsn.ToString(CultureInfo.InvariantCulture)));
                throw ex;
            }
            bool began = false;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "BeginTransaction");

                // Enterprise policy: disallow transactions when unauthenticated.
                if (string.IsNullOrEmpty(_principal))
                    throw new SessionLifecycleInvalidException(State, "BeginTransactionUnauthenticated");


                if (CurrentTransactionId.HasValue && TransactionState == TransactionState.Active)
                    throw new TransactionAlreadyActiveException();

                CurrentTransactionId = txId;
                SnapshotLsn = snapshotLsn;
                TransactionStartUtc = DateTime.UtcNow;
                TransactionState = TransactionState.Active;
                _txStartTicks = Stopwatch.GetTimestamp();
                began = true;
            }
            if (began)
            {
                // Preserve current behavior: promote to Active on activity
                Touch();
            }
            SessionMetrics.IncrementTxBegin(_metrics);
            SessionDiagnostics.Emit(_componentName, "BeginTransaction", "start",
                "tx_begin",
                null,
                CreateSessionTagMapWith("tx", txId.ToString(CultureInfo.InvariantCulture),
                                        "snapshot_lsn", snapshotLsn.ToString(CultureInfo.InvariantCulture)));
        }

        public void EndTransaction(TransactionState finalState)
        {
            if (finalState == TransactionState.Active)
                throw new TransactionFinalStateInvalidException(finalState);

            double? latencyMs = null;
            long? startTicks = null;
            long? txIdForEmit = null;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "EndTransaction");
                if (!CurrentTransactionId.HasValue)
                {
                    TransactionState = TransactionState.None;
                    TransactionStartUtc = null;
                    SnapshotLsn = null;
                    _txStartTicks = null;
                    SessionDiagnostics.EmitDebug(_componentName, "EndTransaction", "noop_no_active_tx", CreateSessionTagMap());
                    return;
                }
                // Require a concrete terminal state when ending an active transaction
                if (finalState == TransactionState.None)
                    throw new TransactionFinalStateInvalidException(finalState);
                var started = TransactionStartUtc;
                startTicks = _txStartTicks;
                txIdForEmit = CurrentTransactionId;
                TransactionState = finalState;
                CurrentTransactionId = null;
                TransactionStartUtc = null;
                SnapshotLsn = null;
                _txStartTicks = null;
                if (started.HasValue)
                {
                    // Prefer monotonic stopwatch when available; fall back to wall-clock.
                    if (startTicks.HasValue)
                    {
                        // .NET 8 precise monotonic elapsed time
                        var elapsed = Stopwatch.GetElapsedTime(startTicks.Value);
                        latencyMs = elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        latencyMs = (DateTime.UtcNow - started.Value).TotalMilliseconds;
                    }
                }
            }
            // Metrics: count + latency when available
            SessionMetrics.IncrementTxEnd(_metrics, finalState.ToString());
            if (latencyMs.HasValue) SessionMetrics.RecordTxLatency(_metrics, latencyMs.Value);
            var more = CreateSessionTagMapWith("state", finalState.ToString());
            if (txIdForEmit.HasValue)
            {
                more["tx"] = txIdForEmit.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (latencyMs.HasValue)
            {
                more["latency_ms"] = latencyMs.Value.ToString(CultureInfo.InvariantCulture);
            }
            SessionDiagnostics.Emit(_componentName, "EndTransaction",
                finalState == TransactionState.Committed ? "ok" :
                (finalState == TransactionState.RolledBack || finalState == TransactionState.Aborted) ? "warn" : "info",
                "tx_end",
                null,
                more);
        }

        public SessionSnapshot ReadSnapshot()
        {
            // Build participant snapshot under lock to ensure consistency with other fields
            List<string> participants;
            string principal;
            string coordinator;
            SessionState state;
            DateTime created;
            DateTime last;
            long? currentTx;
            TransactionState txState;
            DateTime? txStart;
            IsolationLevel iso;
            long? snapLsn;
            string? query;
            long? globalTx;
            lock (_stateLock)
            {
                participants = new List<string>(_participantNodes.Count);
                for (int i = 0; i < _participantNodes.Count; i++) participants.Add(_participantNodes[i]);
                principal = _principal;
                coordinator = _coordinatorNode;
                state = State;
                created = CreatedUtc;
                last = LastActivityUtc;
                currentTx = CurrentTransactionId;
                txState = TransactionState;
                txStart = TransactionStartUtc;
                iso = IsolationLevel;
                snapLsn = SnapshotLsn;
                query = CurrentQuery;
                globalTx = GlobalTransactionId;
            }
            return new SessionSnapshot(
                SessionId,
                GlobalSessionId,
                principal,
                state,
                created,
                last,
                currentTx,
                txState,
                txStart,
                iso,
                snapLsn,
                IdleTimeout,
                TransactionTimeout,
                query,
                coordinator,
                new ReadOnlyCollection<string>(participants),
                globalTx);
        }
        public void SetCurrentQuery(string? queryText)
        {
            bool hasMeaningfulContent = false;
            bool clearedDueToWhitespace = false;
            bool wasTruncated = false;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "SetCurrentQuery");
                if (queryText is null)
                {
                    CurrentQuery = null;
                }
                else
                {
                    // Assess once: meaningful content vs whitespace-only
                    bool allWs = true;
                    for (int i = 0; i < queryText.Length; i++)
                    {
                        if (!char.IsWhiteSpace(queryText[i])) { allWs = false; break; }
                    }
                    if (allWs)
                    {
                        CurrentQuery = null;
                        clearedDueToWhitespace = true;
                    }
                    else
                    {
                        hasMeaningfulContent = true;
                        // Cap query length to avoid unbounded memory growth
                        if (queryText.Length > MaxQueryLength)
                        {
                            // Truncate deterministically
                            CurrentQuery = queryText.Substring(0, MaxQueryLength);
                            wasTruncated = true;
                        }
                        else
                        {
                            CurrentQuery = queryText;
                        }
                    }
                }
            }
            // Touch only on meaningful non-whitespace query content
            if (hasMeaningfulContent) Touch();
            SessionMetrics.IncrementQuerySet(_metrics);
            // Emit truthful low-cardinality signals: len for set, cleared when null or whitespace-only
            if (queryText is null || clearedDueToWhitespace)
            {
                SessionDiagnostics.EmitDebug(_componentName, "SetCurrentQuery",
                    "query_set",
                    CreateSessionTagMapWith("cleared", "yes"));
            }
            else
            {
                SessionDiagnostics.EmitDebug(_componentName, "SetCurrentQuery",
                    "query_set",
                    CreateSessionTagMapWith("len", queryText.Length.ToString(CultureInfo.InvariantCulture)));
            }
            if (wasTruncated)
            {
                SessionDiagnostics.EmitDebug(_componentName, "SetCurrentQuery",
                    "query_truncated",
                    CreateSessionTagMapWith("original_len", queryText!.Length.ToString(CultureInfo.InvariantCulture)));
            }
        }

        public void AddDiagnosticTag(string key, string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));
            // Normalize inputs: trim and disallow empty/whitespace keys to preserve filterability.
            string normalizedKey = key.Trim();
            if (normalizedKey.Length == 0 || IsAllWhitespace(normalizedKey))
                throw new ArgumentException("Key must be non-empty and not whitespace-only.", nameof(key));
            // Cap key length deterministically to avoid unbounded growth
            if (normalizedKey.Length > MaxDiagnosticTagKeyLength)
            {
                var originalLen = normalizedKey.Length;
                normalizedKey = normalizedKey.Substring(0, MaxDiagnosticTagKeyLength);
                SessionDiagnostics.EmitDebug(_componentName, "AddDiagnosticTag",
                    "tag_key_truncated",
                    CreateSessionTagMapWith("original_len", originalLen.ToString(CultureInfo.InvariantCulture)));
            }
            // Normalize value: trim and cap deterministically to avoid unbounded growth
            string normalizedValue = value.Trim();
            if (normalizedValue.Length > MaxDiagnosticTagValueLength)
            {
                var originalLen = normalizedValue.Length;
                normalizedValue = normalizedValue.Substring(0, MaxDiagnosticTagValueLength);
                SessionDiagnostics.EmitDebug(_componentName, "AddDiagnosticTag",
                    "tag_value_truncated",
                    CreateSessionTagMapWith("original_len", originalLen.ToString(CultureInfo.InvariantCulture)));
            }
            bool added = false;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "AddDiagnosticTag");
                // Update existing keys always allowed
                if (_diagnosticTags.ContainsKey(normalizedKey))
                {
                    _diagnosticTags[normalizedKey] = normalizedValue;
                    added = true;
                }
                else
                {
                    // Only allow new key if under cap
                    if (_diagnosticTags.Count < MaxDiagnosticTags)
                    {
                        _diagnosticTags[normalizedKey] = normalizedValue;
                        added = true;
                    }
                }
            }
            if (!added)
            {
                // Refuse silently at state level; emit a warn trace for operational visibility
                var more = CreateSessionTagMapWith("cap", MaxDiagnosticTags.ToString(CultureInfo.InvariantCulture));
                lock (_stateLock)
                {
                    more["count"] = _diagnosticTags.Count.ToString(CultureInfo.InvariantCulture);
                }
                SessionDiagnostics.Emit(_componentName, "AddDiagnosticTag", "warn",
                    "tag_cap_reached",
                    null,
                    more);
                return;
            }
            SessionMetrics.IncrementTagSet(_metrics);
            SessionDiagnostics.EmitDebug(_componentName, "AddDiagnosticTag",
                "tag_set",
                CreateSessionTagMapWith("key", normalizedKey));
        }

        public void RemoveDiagnosticTag(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            // Normalize and reject whitespace-only keys to avoid ambiguous removals.
            string trimmedKey = key.Trim();
            if (trimmedKey.Length == 0 || IsAllWhitespace(trimmedKey))
                throw new ArgumentException("Key must be non-empty and not whitespace-only.", nameof(key));
            int removed = 0;
            string normalizedKey = trimmedKey;
            if (normalizedKey.Length > MaxDiagnosticTagKeyLength)
            {
                // Apply same deterministic cap used for AddDiagnosticTag so removals match truncated keys.
                normalizedKey = normalizedKey.Substring(0, MaxDiagnosticTagKeyLength);
            }
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "RemoveDiagnosticTag");
                if (_diagnosticTags.ContainsKey(normalizedKey))
                {
                    _diagnosticTags.Remove(normalizedKey);
                    removed = 1;
                }
            }
            if (removed > 0)
            {
                SessionMetrics.IncrementTagRemove(_metrics, removed);
                SessionDiagnostics.EmitDebug(_componentName, "RemoveDiagnosticTag",
                    "tag_removed",
                    CreateSessionTagMapWith("key", normalizedKey));
            }
            else
            {
                SessionDiagnostics.EmitDebug(_componentName, "RemoveDiagnosticTag",
                    "tag_remove_noop_not_found",
                    CreateSessionTagMapWith("key", normalizedKey));
            }
        }

        public void ClearDiagnosticTags()
        {
            int count = 0;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "ClearDiagnosticTags");
                count = _diagnosticTags.Count;
                if (count > 0) _diagnosticTags.Clear();
            }
            if (count > 0)
            {
                SessionMetrics.IncrementTagRemove(_metrics, count);
                SessionDiagnostics.EmitDebug(_componentName, "ClearDiagnosticTags",
                    "tags_cleared",
                    CreateSessionTagMapWith("count", count.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                SessionDiagnostics.EmitDebug(_componentName, "ClearDiagnosticTags",
                    "tags_clear_noop_empty",
                    CreateSessionTagMap());
            }
        }

        // Distributed helpers
        public void SetCoordinatorNode(string nodeName)
        {
            // Normalize to empty and reject whitespace-only to avoid misleading values.
            if (nodeName == null) { nodeName = string.Empty; }
            else
            {
                // Trim first to avoid accidental trailing/leading whitespace diversity
                nodeName = nodeName.Trim();
                // After Trim(), a non-empty string necessarily has non-whitespace content.
                if (nodeName.Length == 0)
                {
                    nodeName = string.Empty;
                }
            }
            // Cap coordinator node name length deterministically to avoid unbounded growth
            if (nodeName.Length > MaxCoordinatorNameLength)
            {
                var originalLen = nodeName.Length;
                nodeName = nodeName.Substring(0, MaxCoordinatorNameLength);
                SessionDiagnostics.EmitDebug(_componentName, "SetCoordinatorNode",
                    "coordinator_truncated",
                    CreateSessionTagMapWith("original_len", originalLen.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "SetCoordinatorNode");
                _coordinatorNode = nodeName;
            }
            SessionDiagnostics.EmitDebug(_componentName, "SetCoordinatorNode",
                "coordinator_set",
                CreateSessionTagMapWith("coordinator", _coordinatorNode));
        }
        public void SetGlobalTransaction(long gtid)
        {
            if (gtid <= 0) throw new ArgumentOutOfRangeException(nameof(gtid));
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "SetGlobalTransaction");
                GlobalTransactionId = gtid;
            }
            SessionDiagnostics.EmitDebug(_componentName, "SetGlobalTransaction",
                "gtid_set",
                CreateSessionTagMapWith("gtid", gtid.ToString(CultureInfo.InvariantCulture)));
        }
        public void ClearGlobalTransaction()
        {
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "ClearGlobalTransaction");
                GlobalTransactionId = null;
            }
            SessionDiagnostics.EmitDebug(_componentName, "ClearGlobalTransaction",
                "gtid_cleared",
                CreateSessionTagMap());
        }
        public void AddParticipantNode(string nodeName)
        {
            if (nodeName == null) throw new ArgumentNullException(nameof(nodeName));
            // Trim first; avoid adding whitespace-only node names.
            nodeName = nodeName.Trim();
            if (nodeName.Length == 0 || IsAllWhitespace(nodeName))
                throw new ArgumentException("Node name must be non-empty and not whitespace-only.", nameof(nodeName));
            // Cap participant node name length deterministically for safety
            if (nodeName.Length > MaxParticipantNameLength)
            {
                var originalLen = nodeName.Length;
                nodeName = nodeName.Substring(0, MaxParticipantNameLength);
                SessionDiagnostics.EmitDebug(_componentName, "AddParticipantNode",
                    "participant_truncated",
                    CreateSessionTagMapWith("original_len", originalLen.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            bool added = false;
            bool duplicate = false;
            bool capReached = false;
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "AddParticipantNode");
                // Deduplicate without LINQ; case-insensitive for operational clarity
                bool exists = false;
                for (int i = 0; i < _participantNodes.Count; i++)
                {
                    if (string.Equals(_participantNodes[i], nodeName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    if (_participantNodes.Count < MaxParticipantNodes)
                    {
                        _participantNodes.Add(nodeName);
                        added = true;
                    }
                    else
                    {
                        capReached = true;
                    }
                }
                else
                {
                    duplicate = true;
                }
            }
            if (!added)
            {
                if (duplicate)
                {
                    // Duplicate participant suppressed; low-noise debug signal.
                    SessionDiagnostics.EmitDebug(_componentName, "AddParticipantNode",
                        "participant_duplicate",
                        CreateSessionTagMapWith("node", nodeName));
                    return;
                }
                if (capReached)
                {
                    // Cap reached: elevate to warn for operational visibility.
                    SessionDiagnostics.Emit(_componentName, "AddParticipantNode", "warn",
                        "participant_cap_reached",
                        null,
                        CreateSessionTagMapWith("cap", MaxParticipantNodes.ToString(CultureInfo.InvariantCulture)));
                    return;
                }
                // Defensive: unexpected path; keep a low-noise debug.
                SessionDiagnostics.EmitDebug(_componentName, "AddParticipantNode",
                    "participant_not_added_unknown_reason",
                    CreateSessionTagMapWith("node", nodeName));
                return;
            }
            SessionDiagnostics.EmitDebug(_componentName, "AddParticipantNode",
                "participant_add",
                CreateSessionTagMapWith("node", nodeName));
        }
        public void ClearParticipantNodes()
        {
            lock (_stateLock)
            {
                if (State == SessionState.Closed || State == SessionState.Expired)
                    throw new SessionLifecycleInvalidException(State, "ClearParticipantNodes");
                _participantNodes.Clear();
            }
            SessionDiagnostics.EmitDebug(_componentName, "ClearParticipantNodes",
                "participant_cleared",
                CreateSessionTagMap());
        }

        // Internal lifecycle transitions
        void ISessionContextInternal.MarkIdle()
        {
            // Do not mark Idle while a transaction is active. "Idle" implies no ongoing work.
            bool txActive;
            bool changedToIdle = false;
            lock (_stateLock)
            {
                txActive = (TransactionState == TransactionState.Active);
                if (!txActive && State != SessionState.Idle)
                {
                    State = SessionState.Idle;
                    SessionMetrics.IncrementStateTransition(_metrics, "Idle");
                    changedToIdle = true;
                }
            }
            if (txActive)
            {
                // Low-noise debug for admin-plane clarity.
                SessionDiagnostics.EmitDebug(_componentName, "MarkIdle",
                    "skip_idle_while_tx_active",
                    CreateSessionTagMap());
                return;
            }
            if (changedToIdle)
            {
                SessionDiagnostics.EmitDebug(_componentName, "MarkIdle",
                    "state_idle",
                    CreateSessionTagMap());
            }
            else
            {
                SessionDiagnostics.EmitDebug(_componentName, "MarkIdle",
                    "idle_noop_already_idle",
                    CreateSessionTagMap());
            }
        }
        void ISessionContextInternal.MarkClosed()
        {
            lock (_stateLock)
            {
                if (State != SessionState.Closed)
                {
                    State = SessionState.Closed;
                    SessionMetrics.IncrementStateTransition(_metrics, "Closed");
                }
                // Purge volatile state immediately upon terminal transition
                ((ISessionContextInternal)this).PurgeVolatileAfterTerminal();
            }
            SessionDiagnostics.Emit(_componentName, "MarkClosed",
                "ok",
                "state_closed",
                null,
                CreateSessionTagMap());
        }
        void ISessionContextInternal.MarkExpired()
        {
            lock (_stateLock)
            {
                if (State != SessionState.Expired)
                {
                    State = SessionState.Expired;
                    SessionMetrics.IncrementStateTransition(_metrics, "Expired");
                }
                // Purge volatile state immediately upon terminal transition
                ((ISessionContextInternal)this).PurgeVolatileAfterTerminal();
            }
            SessionDiagnostics.Emit(_componentName, "MarkExpired",
                "warn",
                "state_expired",
                null,
                CreateSessionTagMap());
        }

        // Deterministic timeout checks using monotonic ticks. Fall back to wall-clock if needed.
        bool ISessionContextInternal.IsIdleExpiredUtc(DateTime utcNow)
        {
            // Snapshot fields under lock for visibility and multi-field consistency.
            TimeSpan idleTimeout;
            long lastActivityTicks;
            DateTime lastActivityUtc;
            lock (_stateLock)
            {
                idleTimeout = IdleTimeout;
                lastActivityTicks = _lastActivityTicks;
                lastActivityUtc = LastActivityUtc;
            }
            // Zero idle timeout disables eviction
            if (idleTimeout == TimeSpan.Zero) return false;
            // Monotonic path using precise elapsed calculation
            var elapsed = Stopwatch.GetElapsedTime(lastActivityTicks);
            if (elapsed >= idleTimeout) return true;
            // Secondary fallback for catastrophic tick resets: also check wall-clock
            var cutoff = lastActivityUtc.Add(idleTimeout);
            return utcNow >= cutoff;
        }

        bool ISessionContextInternal.IsTransactionTimedOutUtc(DateTime utcNow)
        {
            // Snapshot fields under lock for visibility and multi-field consistency.
            TimeSpan txTimeout;
            TransactionState txState;
            DateTime? txStartUtc;
            long? txStartTicks;
            lock (_stateLock)
            {
                txTimeout = TransactionTimeout;
                txState = TransactionState;
                txStartUtc = TransactionStartUtc;
                txStartTicks = _txStartTicks;
            }
            if (txTimeout == TimeSpan.Zero) return false;
            // Only applicable when a transaction is active
            if (txState != TransactionState.Active || !txStartUtc.HasValue) return false;
            if (txStartTicks.HasValue)
            {
                var elapsed = Stopwatch.GetElapsedTime(txStartTicks.Value);
                if (elapsed >= txTimeout) return true;
            }
            // Fallback to wall-clock cutoff if needed
            var cutoff = txStartUtc.Value.Add(txTimeout);
            return utcNow >= cutoff;
        }

        // Terminal purge that does not throw lifecycle exceptions
        void ISessionContextInternal.PurgeVolatileAfterTerminal()
        {
            // Clear volatile state: tags, query, participants, global transaction id.
            // Transaction anchor fields are already nulled by EndTransaction; we do not
            // call public mutators to avoid lifecycle exceptions post-terminal.
            // Self-lock for future-proofing; Monitor is re-entrant for same-thread paths.
            lock (_stateLock)
            {
                _diagnosticTags.Clear();
                _participantNodes.Clear();
                CurrentQuery = null;
                GlobalTransactionId = null;
                // Anchor and timing fields: defensively clear.
                SnapshotLsn = null;
                CurrentTransactionId = null;
                TransactionStartUtc = null;
                _txStartTicks = null;
                // Ensure terminal consistency: no transaction reported on CLOSED/EXPIRED sessions.
                TransactionState = Contracts.TransactionState.None;
            }
        }

        // Local helpers: small tag maps for trace filterability; no LINQ, low overhead.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Dictionary<string, string> CreateSessionTagMap()
        {
            var d = CreateSessionTagMapCore();
            return d;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Dictionary<string, string> CreateSessionTagMapWith(string k1, string v1)
        {
            var d = CreateSessionTagMapCore();
            d[k1] = v1;
            return d;
        }
        private Dictionary<string, string> CreateSessionTagMapWith(string k1, string v1, string k2, string v2)
        {
            var d = CreateSessionTagMapCore();
            d[k1] = v1;
            d[k2] = v2;
            return d;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Dictionary<string, string> CreateSessionTagMapCore()
        {
            var d = new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
            // Use log‑safe surrogate for session.id
            d["session.id"] = LogId;
            // GlobalSessionId is not a credential, safe to log raw
            d["global.session.id"] = GlobalSessionId.ToString();
            // Low-cardinality signal to avoid leaking PII
            if (!string.IsNullOrEmpty(_principal)) d["principal.present"] = "yes";
            else d["principal.present"] = "no";
            // Low-cardinality live state for triage clarity
            d["session.state"] = State.ToString();
            d["tx.state"] = TransactionState.ToString();
            return d;
        }

        // Utility: check if a string is entirely whitespace without LINQ.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAllWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i])) return false;
            }
            return true;
        }
    }
}
