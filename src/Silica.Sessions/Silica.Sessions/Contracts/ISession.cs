using Silica.Sessions.Contracts;

namespace Silica.Sessions
{
    /// <summary>
    /// Canonical Silica session abstraction.
    /// Frontends (HTTP, TCP, CLI) adapt into this contract.
    /// </summary>
    public interface ISession
    {
        // Identity
        SessionId Id { get; }
        string? Principal { get; }

        // Lifecycle
        SessionState State { get; }
        DateTime CreatedUtc { get; }
        DateTime LastTouchedUtc { get; }

        // Transaction anchor
        TransactionState TransactionState { get; }
        IsolationLevel IsolationLevel { get; }

        // Operations
        void Touch(); // refresh idle timer
        void Authenticate(string principal);
        void BeginTransaction(IsolationLevel isolation);
        void EndTransaction(bool commit);

        // Metadata
        void SetTag(string key, string value);
        bool TryGetTag(string key, out string? value);
        void RemoveTag(string key);

        // Diagnostics
        SessionSnapshot Snapshot();
    }
}
