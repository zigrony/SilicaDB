// LockExceptions.cs
using System;

namespace Silica.Concurrency
{
    /// <summary>
    /// Thrown when a transaction attempts to upgrade from shared to exclusive
    /// while not being the sole holder, which would cause a self-deadlock.
    /// </summary>
    public sealed class LockUpgradeIncompatibleException : InvalidOperationException
    {
        public long TransactionId { get; }
        public string ResourceId { get; }
        public LockUpgradeIncompatibleException(long txId, string resourceId)
            : base("Upgrade to exclusive would self-deadlock: transaction is not the sole holder.")
        {
            TransactionId = txId;
            ResourceId = resourceId ?? string.Empty;
        }
    }
}
