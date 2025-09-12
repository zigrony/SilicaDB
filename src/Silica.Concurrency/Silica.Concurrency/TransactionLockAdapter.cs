// Filename: Silica.Concurrency/TransactionLockAdapter.cs
using System;
using System.Data; // ships with .NET
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Concurrency
{
    /// <summary>
    /// Thin transaction-aware adapter that maps IsolationLevel to LockMode
    /// and delegates to LockManager with the caller's transactionId.
    /// No LINQ, no reflection, no JSON, no third-party libraries.
    /// </summary>
    public sealed class TransactionLockAdapter
    {
        private readonly LockManager _locks;

        public TransactionLockAdapter(LockManager lockManager)
        {
            _locks = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        }

        /// <summary>
        /// Acquire a lock for the given transaction and resource, based on isolation level.
        /// Returns a LockGrant with Granted flag and fencing token if granted.
        /// </summary>
        public Task<LockGrant> AcquireAsync(
            long transactionId,
            string resourceId,
            IsolationLevel isolationLevel,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            var mode = MapIsolationToMode(isolationLevel);
            if (mode == LockMode.Shared)
            {
                return _locks.AcquireSharedAsync(transactionId, resourceId, timeoutMilliseconds, cancellationToken);
            }
            else
            {
                return _locks.AcquireExclusiveAsync(transactionId, resourceId, timeoutMilliseconds, cancellationToken);
            }
        }

        /// <summary>
        /// Release a previously acquired lock for the given transaction and resource.
        /// The fencing token must match the token returned by AcquireAsync.
        /// </summary>
        public Task ReleaseAsync(
            long transactionId,
            string resourceId,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            return _locks.ReleaseAsync(transactionId, resourceId, fencingToken, cancellationToken);
        }

        /// <summary>
        /// Optimistic, no-queue probe for acquiring a lock based on isolation level.
        /// Returns true and a fencing token if granted immediately; false otherwise.
        /// </summary>
        public bool TryAcquire(
            long transactionId,
            string resourceId,
            IsolationLevel isolationLevel,
            out long fencingToken)
        {
            var mode = MapIsolationToMode(isolationLevel);
            if (mode == LockMode.Shared)
            {
                return _locks.TryAcquireShared(transactionId, resourceId, out fencingToken);
            }
            else
            {
                return _locks.TryAcquireExclusive(transactionId, resourceId, out fencingToken);
            }
        }

        /// <summary>
        /// Forcefully abort a transaction: cancels waits, releases any held locks,
        /// and prunes fencing tokens for the transaction.
        /// </summary>
        public void AbortTransaction(long transactionId)
        {
            _locks.AbortTransaction(transactionId);
        }

        /// <summary>
        /// Explicit IsolationLevel -> LockMode mapping (no LINQ/reflection).
        /// </summary>
        private static LockMode MapIsolationToMode(IsolationLevel level)
        {
            // Reads that can coexist map to Shared.
            // Levels that imply write locks or stronger guarantees map to Exclusive.
            switch (level)
            {
                case IsolationLevel.ReadUncommitted:
                case IsolationLevel.ReadCommitted:
                case IsolationLevel.Snapshot:
                    return LockMode.Shared;

                case IsolationLevel.RepeatableRead:
                case IsolationLevel.Serializable:
                case IsolationLevel.Chaos:
                case IsolationLevel.Unspecified:
                default:
                    return LockMode.Exclusive;
            }
        }
    }
}
