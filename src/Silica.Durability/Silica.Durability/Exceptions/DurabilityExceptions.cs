// Filename: Silica.Durability/Exceptions/DurabilityExceptions.cs
using System;
using Silica.Exceptions;

namespace Silica.Durability
{
    /// <summary>
    /// Canonical, low-cardinality Durability exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class DurabilityExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for durability: 2000–2999
            public const int WalNotStarted = 2001;
            public const int WalAlreadyStarted = 2002;
            public const int WalDisposed = 2003;
            public const int PayloadTooLarge = 2004;
            public const int WalSequenceOverflow = 2005;

            public const int CheckpointManagerNotStarted = 2010;
            public const int CheckpointManagerDisposed = 2011;
            public const int CheckpointNotWritableState = 2012;
            public const int CheckpointFileCorrupt = 2013;
            public const int CheckpointRenameNotVisible = 2014;
            public const int CheckpointReadFailed = 2015;
            public const int CheckpointManagerAlreadyStarted = 2016;

            public const int RecoverManagerNotStarted = 2020;
            public const int RecoverManagerDisposed = 2021;
            public const int RecoverManagerAlreadyStarted = 2022;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        public const int MinId = 2000;
        public const int MaxId = 2999;

        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Durability.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------
        public static readonly ExceptionDefinition WalNotStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.WAL.NOT_STARTED",
                exceptionTypeName: typeof(WalNotStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "Operation attempted on a WAL manager that is not started."
            );

        public static readonly ExceptionDefinition WalAlreadyStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.WAL.ALREADY_STARTED",
                exceptionTypeName: typeof(WalAlreadyStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "WAL manager already started."
            );

        public static readonly ExceptionDefinition WalDisposed =
            ExceptionDefinition.Create(
                code: "DURABILITY.WAL.DISPOSED",
                exceptionTypeName: typeof(WalManagerDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed WAL manager."
            );

        public static readonly ExceptionDefinition PayloadTooLarge =
            ExceptionDefinition.Create(
                code: "DURABILITY.WAL.PAYLOAD_TOO_LARGE",
                exceptionTypeName: typeof(PayloadTooLargeException).FullName!,
                category: FailureCategory.Validation,
                description: "Payload length exceeds maximum allowed size."
            );

        public static readonly ExceptionDefinition WalSequenceOverflow =
            ExceptionDefinition.Create(
                code: "DURABILITY.WAL.SEQUENCE_OVERFLOW",
                exceptionTypeName: typeof(WalSequenceOverflowException).FullName!,
                category: FailureCategory.Internal,
                description: "WAL sequence number overflow: cannot allocate a new LSN."
            );

        public static readonly ExceptionDefinition CheckpointManagerNotStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.NOT_STARTED",
                exceptionTypeName: typeof(CheckpointManagerNotStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "Operation attempted on a CheckpointManager that is not started."
            );

        public static readonly ExceptionDefinition CheckpointManagerDisposed =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.DISPOSED",
                exceptionTypeName: typeof(CheckpointManagerDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed CheckpointManager."
            );

        public static readonly ExceptionDefinition CheckpointNotWritableState =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.NOT_WRITABLE",
                exceptionTypeName: typeof(CheckpointNotWritableStateException).FullName!,
                category: FailureCategory.Internal,
                description: "CheckpointManager not in a writable state."
            );

        public static readonly ExceptionDefinition CheckpointFileCorrupt =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.FILE_CORRUPT",
                exceptionTypeName: typeof(CheckpointFileCorruptException).FullName!,
                category: FailureCategory.Internal,
                description: "Checkpoint file is corrupt or has unexpected length."
            );

        public static readonly ExceptionDefinition CheckpointRenameNotVisible =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.RENAME_NOT_VISIBLE",
                exceptionTypeName: typeof(CheckpointRenameNotVisibleException).FullName!,
                category: FailureCategory.Internal,
                description: "Checkpoint rename not visible after retry."
            );

        public static readonly ExceptionDefinition CheckpointReadFailed =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.READ_FAILED",
                exceptionTypeName: typeof(CheckpointReadFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to read latest checkpoint after retries."
            );

        public static readonly ExceptionDefinition CheckpointManagerAlreadyStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.CHECKPOINT.ALREADY_STARTED",
                exceptionTypeName: typeof(CheckpointManagerAlreadyStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "CheckpointManager already started."
            );

        public static readonly ExceptionDefinition RecoverManagerNotStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.RECOVER.NOT_STARTED",
                exceptionTypeName: typeof(RecoverManagerNotStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "Operation attempted on a RecoverManager that is not started."
            );

        public static readonly ExceptionDefinition RecoverManagerDisposed =
            ExceptionDefinition.Create(
                code: "DURABILITY.RECOVER.DISPOSED",
                exceptionTypeName: typeof(RecoverManagerDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed RecoverManager."
            );

        public static readonly ExceptionDefinition RecoverManagerAlreadyStarted =
            ExceptionDefinition.Create(
                code: "DURABILITY.RECOVER.ALREADY_STARTED",
                exceptionTypeName: typeof(RecoverManagerAlreadyStartedException).FullName!,
                category: FailureCategory.Validation,
                description: "RecoverManager already started."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                WalNotStarted,
                WalAlreadyStarted,
                WalDisposed,
                PayloadTooLarge,
                WalSequenceOverflow,
                CheckpointManagerNotStarted,
                CheckpointManagerDisposed,
                CheckpointNotWritableState,
                CheckpointFileCorrupt,
                CheckpointRenameNotVisible,
                CheckpointReadFailed,
                CheckpointManagerAlreadyStarted,
                RecoverManagerNotStarted,
                RecoverManagerDisposed,
                RecoverManagerAlreadyStarted
            });
        }
    }
}
