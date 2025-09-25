using System;
using System.Collections.Generic;
using Silica.Sessions.Contracts;

namespace Silica.Sessions.Contracts
{
    public interface ISessionContext
    {
        // Identity & lifecycle
        Guid SessionId { get; }
        Guid GlobalSessionId { get; }
        string Principal { get; }
        SessionState State { get; }
        DateTime CreatedUtc { get; }
        DateTime LastActivityUtc { get; }

        // Transaction anchor
        long? CurrentTransactionId { get; }
        TransactionState TransactionState { get; }
        DateTime? TransactionStartUtc { get; }
        IsolationLevel IsolationLevel { get; }
        long? SnapshotLsn { get; }

        // Timeouts
        TimeSpan IdleTimeout { get; }
        TimeSpan TransactionTimeout { get; }

        // Diagnostics
        string? CurrentQuery { get; }
        IReadOnlyDictionary<string, string> DiagnosticTags { get; }
        int DiagnosticTagCount { get; }

        // Distributed (future-proof)
        string CoordinatorNode { get; }
        IReadOnlyList<string> ParticipantNodes { get; }
        long? GlobalTransactionId { get; }
        int ParticipantCount { get; }

        // Lifecycle ops
        void Touch();
        void Authenticate(string principal);
        void SetIsolationLevel(IsolationLevel level);
        void BeginTransaction(long txId, long snapshotLsn);
        void EndTransaction(TransactionState finalState);
        void SetCurrentQuery(string? queryText);
        void AddDiagnosticTag(string key, string value);
        void RemoveDiagnosticTag(string key);
        void ClearDiagnosticTags();

        // Distributed operations (contract-first, future-proof)
        void SetCoordinatorNode(string nodeName);
        void SetGlobalTransaction(long gtid);
        void ClearGlobalTransaction();
        void AddParticipantNode(string nodeName);
        void ClearParticipantNodes();

        // Snapshot (atomic view under lock for multi-field consistency)
        SessionSnapshot ReadSnapshot();
    }
}

namespace Silica.Sessions.Contracts
{
    /// <summary>
    /// Immutable snapshot of a session's state, taken under lock for consistency.
    /// </summary>
    public readonly struct SessionSnapshot
    {
        public readonly Guid SessionId;
        public readonly Guid GlobalSessionId;
        public readonly string Principal;
        public readonly SessionState State;
        public readonly DateTime CreatedUtc;
        public readonly DateTime LastActivityUtc;
        public readonly long? CurrentTransactionId;
        public readonly TransactionState TransactionState;
        public readonly DateTime? TransactionStartUtc;
        public readonly IsolationLevel IsolationLevel;
        public readonly long? SnapshotLsn;
        public readonly TimeSpan IdleTimeout;
        public readonly TimeSpan TransactionTimeout;
        public readonly string? CurrentQuery;
        public readonly string CoordinatorNode;
        public readonly IReadOnlyList<string> ParticipantNodes;
        public readonly long? GlobalTransactionId;

        public SessionSnapshot(
            Guid sessionId,
            Guid globalSessionId,
            string principal,
            SessionState state,
            DateTime createdUtc,
            DateTime lastActivityUtc,
            long? currentTxId,
            TransactionState txState,
            DateTime? txStartUtc,
            IsolationLevel isolation,
            long? snapshotLsn,
            TimeSpan idleTimeout,
            TimeSpan txTimeout,
            string? currentQuery,
            string coordinatorNode,
            IReadOnlyList<string> participantNodes,
            long? globalTxId)
        {
            SessionId = sessionId;
            GlobalSessionId = globalSessionId;
            Principal = principal ?? string.Empty;
            State = state;
            CreatedUtc = createdUtc;
            LastActivityUtc = lastActivityUtc;
            CurrentTransactionId = currentTxId;
            TransactionState = txState;
            TransactionStartUtc = txStartUtc;
            IsolationLevel = isolation;
            SnapshotLsn = snapshotLsn;
            IdleTimeout = idleTimeout;
            TransactionTimeout = txTimeout;
            CurrentQuery = currentQuery;
            CoordinatorNode = coordinatorNode ?? string.Empty;
            ParticipantNodes = participantNodes ?? Array.Empty<string>();
            GlobalTransactionId = globalTxId;
        }
    }
}

// Internal kernel-only lifecycle interface to avoid concrete coupling.
namespace Silica.Sessions.Contracts
{
    internal interface ISessionContextInternal
    {
        void MarkIdle();
        void MarkClosed();
        void MarkExpired();
        // Terminal cleanup: clear volatile fields (tags, query, participants, gtid)
        // without surfacing lifecycle exceptions.
        void PurgeVolatileAfterTerminal();
        // Deterministic timeout checks (monotonic). Callers pass utcNow for fallback paths.
        // Implementations should use Stopwatch ticks when available.
        bool IsIdleExpiredUtc(DateTime utcNow);
        bool IsTransactionTimedOutUtc(DateTime utcNow);
    }
}
