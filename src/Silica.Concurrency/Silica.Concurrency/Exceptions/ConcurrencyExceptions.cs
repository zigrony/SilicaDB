using System;
using System.Collections.Generic;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    /// <summary>
    /// Canonical, low-cardinality Concurrency exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class ConcurrencyExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for concurrency: 1000–1999
            public const int LockUpgradeIncompatible = 1001;
            public const int InvalidFencingToken = 1002;
            public const int MissingFencingToken = 1003;
            public const int ReleaseNotHolder = 1004;
            public const int ReentrantHoldOverflow = 1005;
            public const int FencingTokenOverflow = 1006;
            public const int LockManagerDisposed = 1007;
            public const int ReservationOverflow = 1008;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Concurrency.
        /// </summary>
        public const int MinId = 1000;
        public const int MaxId = 1999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Concurrency reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Concurrency.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition LockUpgradeIncompatible =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.LOCK.UPGRADE_INCOMPATIBLE",
                exceptionTypeName: typeof(LockUpgradeIncompatibleException).FullName!,
                category: FailureCategory.Validation,
                description: "Upgrade to exclusive would self-deadlock: transaction is not the sole holder."
            );

        public static readonly ExceptionDefinition InvalidFencingToken =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.FENCING.INVALID_TOKEN",
                exceptionTypeName: typeof(InvalidFencingTokenException).FullName!,
                category: FailureCategory.Validation,
                description: "Invalid or stale fencing token supplied for resource release."
            );

        public static readonly ExceptionDefinition MissingFencingToken =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.FENCING.MISSING_TOKEN",
                exceptionTypeName: typeof(MissingFencingTokenException).FullName!,
                category: FailureCategory.Internal,
                description: "Missing fencing token mapping for a non-first hold on the resource."
            );

        public static readonly ExceptionDefinition ReleaseNotHolder =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.RELEASE.NOT_HOLDER",
                exceptionTypeName: typeof(ReleaseNotHolderException).FullName!,
                category: FailureCategory.Validation,
                description: "Release called with a valid fencing token, but the transaction is not a holder of the resource."
            );

        public static readonly ExceptionDefinition ReentrantHoldOverflow =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.HOLD.OVERFLOW",
                exceptionTypeName: typeof(ReentrantHoldOverflowException).FullName!,
                category: FailureCategory.Internal,
                description: "Reentrant hold count overflow for a transaction on a resource."
            );

        public static readonly ExceptionDefinition FencingTokenOverflow =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.FENCING.OVERFLOW",
                exceptionTypeName: typeof(FencingTokenOverflowException).FullName!,
                category: FailureCategory.Internal,
                description: "Per-resource fencing token counter overflowed."
            );

        public static readonly ExceptionDefinition LockManagerDisposed =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.MANAGER.DISPOSED",
                exceptionTypeName: typeof(LockManagerDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed LockManager."
            );

        public static readonly ExceptionDefinition ReservationOverflow =
            ExceptionDefinition.Create(
                code: "CONCURRENCY.WAIT.RESERVATION_OVERFLOW",
                exceptionTypeName: typeof(ReservationOverflowException).FullName!,
                category: FailureCategory.Internal,
                description: "Reserved waiter count overflow for a resource."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                LockUpgradeIncompatible,
                InvalidFencingToken,
                MissingFencingToken,
                ReleaseNotHolder,
                ReentrantHoldOverflow,
                FencingTokenOverflow,
                LockManagerDisposed,
                ReservationOverflow
            });
        }
    }
}
