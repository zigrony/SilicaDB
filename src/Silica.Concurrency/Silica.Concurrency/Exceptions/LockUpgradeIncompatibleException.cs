using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    /// <summary>
    /// Thrown when a transaction attempts to upgrade from shared to exclusive
    /// while not being the sole holder, which would cause a self-deadlock.
    /// </summary>
    public sealed class LockUpgradeIncompatibleException : SilicaException
    {
        public long TransactionId { get; }
        public string ResourceId { get; }

        public LockUpgradeIncompatibleException(long txId, string resourceId)
            : base(
                code: ConcurrencyExceptions.LockUpgradeIncompatible.Code,
                message: "Upgrade to exclusive would self-deadlock: transaction is not the sole holder.",
                category: ConcurrencyExceptions.LockUpgradeIncompatible.Category,
                exceptionId: ConcurrencyExceptions.Ids.LockUpgradeIncompatible)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TransactionId = txId;
            ResourceId = resourceId ?? string.Empty;
        }
    }
}
