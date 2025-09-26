// File: Silica.Sessions/Contracts/ISessionProvider.cs
using System;

namespace Silica.Sessions.Contracts
{
    /// <summary>
    /// Minimal contract for session lifecycle management.
    /// Implementations may provide eviction, timeout enforcement, and diagnostics.
    /// </summary>
    public interface ISessionProvider
    {
        /// <summary>
        /// Creates a new session for the given principal (may be empty to represent unauthenticated).
        /// </summary>
        ISessionContext CreateSession(string? principal, TimeSpan idleTimeout, TimeSpan transactionTimeout, string? coordinatorNode = null);

        /// <summary>
        /// Attempts to resume a session by ID; returns null when not found or expired.
        /// </summary>
        ISessionContext? ResumeSession(Guid sessionId);

        /// <summary>
        /// Invalidates a session (close/expire idempotently). Returns false when not found.
        /// </summary>
        bool InvalidateSession(Guid sessionId);
    }
}
