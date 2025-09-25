using System;

namespace Silica.Sessions.Contracts
{
    public interface ISessionManager
    {
        ISessionContext CreateSession(string? principal, TimeSpan idleTimeout, TimeSpan transactionTimeout, string? coordinatorNode);
        ISessionContext GetSession(Guid sessionId);
        bool TryGetSession(Guid sessionId, out ISessionContext session);
        void Touch(Guid sessionId);
        void SetIdle(Guid sessionId);
        void Close(Guid sessionId);
        void Expire(Guid sessionId);

        int EvictIdleSessions(DateTime utcNow);
        int AbortTimedOutTransactions(DateTime utcNow);

        // Idempotent admin-plane helpers: return false when session not found.
        bool TryClose(Guid sessionId);
        bool TryExpire(Guid sessionId);
        bool TryTouch(Guid sessionId);
        bool TrySetIdle(Guid sessionId);

    }
}
