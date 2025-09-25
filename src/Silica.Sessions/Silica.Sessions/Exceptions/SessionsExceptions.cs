using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    /// <summary>
    /// Canonical, low-cardinality Sessions exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class SessionExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for sessions: 2000–2999
            public const int SessionNotFound = 2001;
            public const int InvalidPrincipal = 2002;
            public const int InvalidTransactionId = 2003;
            public const int TransactionAlreadyActive = 2004;
            public const int TransactionFinalStateInvalid = 2005;
            public const int NegativeTimeout = 2006;
            public const int IdleTimeoutExpired = 2007;
            public const int InvalidSnapshotLsn = 2008;
            public const int SessionLifecycleInvalid = 2009;
            public const int PrincipalChangeNotAllowed = 2010;
            public const int SessionExpired = 2011;
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
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Sessions.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------
        public static readonly ExceptionDefinition SessionNotFound =
            ExceptionDefinition.Create(
                code: "SESSIONS.SESSION.NOT_FOUND",
                exceptionTypeName: typeof(SessionNotFoundException).FullName!,
                category: FailureCategory.Validation,
                description: "Requested session was not found."
            );

        public static readonly ExceptionDefinition InvalidPrincipal =
            ExceptionDefinition.Create(
                code: "SESSIONS.AUTH.INVALID_PRINCIPAL",
                exceptionTypeName: typeof(InvalidPrincipalException).FullName!,
                category: FailureCategory.Validation,
                description: "Principal must be a non-empty string."
            );

        public static readonly ExceptionDefinition InvalidTransactionId =
            ExceptionDefinition.Create(
                code: "SESSIONS.TX.INVALID_ID",
                exceptionTypeName: typeof(InvalidTransactionIdException).FullName!,
                category: FailureCategory.Validation,
                description: "TransactionId must be greater than zero."
            );

        public static readonly ExceptionDefinition TransactionAlreadyActive =
            ExceptionDefinition.Create(
                code: "SESSIONS.TX.ALREADY_ACTIVE",
                exceptionTypeName: typeof(TransactionAlreadyActiveException).FullName!,
                category: FailureCategory.Validation,
                description: "A transaction is already active in this session."
            );

        public static readonly ExceptionDefinition TransactionFinalStateInvalid =
            ExceptionDefinition.Create(
                code: "SESSIONS.TX.FINAL_STATE_INVALID",
                exceptionTypeName: typeof(TransactionFinalStateInvalidException).FullName!,
                category: FailureCategory.Validation,
                description: "Final transaction state cannot be Active when ending a transaction."
            );

        public static readonly ExceptionDefinition NegativeTimeout =
            ExceptionDefinition.Create(
                code: "SESSIONS.TIMEOUT.INVALID",
                exceptionTypeName: typeof(NegativeTimeoutException).FullName!,
                category: FailureCategory.Validation,
                description: "Timeouts must be non-negative."
            );

        public static readonly ExceptionDefinition IdleTimeoutExpired =
            ExceptionDefinition.Create(
                code: "SESSIONS.SESSION.IDLE_EXPIRED",
                exceptionTypeName: typeof(IdleTimeoutExpiredException).FullName!,
                category: FailureCategory.Internal,
                description: "Session expired due to idle timeout."
            );


        public static readonly ExceptionDefinition InvalidSnapshotLsn =
            ExceptionDefinition.Create(
                code: "SESSIONS.TX.INVALID_SNAPSHOT_LSN",
                exceptionTypeName: typeof(InvalidSnapshotLsnException).FullName!,
                category: FailureCategory.Validation,
                description: "Snapshot LSN must be greater than zero."
            );

        public static readonly ExceptionDefinition SessionLifecycleInvalid =
            ExceptionDefinition.Create(
                code: "SESSIONS.SESSION.LIFECYCLE_INVALID",
                exceptionTypeName: typeof(SessionLifecycleInvalidException).FullName!,
                category: FailureCategory.Validation,
                description: "Operation is invalid for the current session lifecycle state."
            );

        public static readonly ExceptionDefinition PrincipalChangeNotAllowed =
            ExceptionDefinition.Create(
                code: "SESSIONS.AUTH.PRINCIPAL_CHANGE_NOT_ALLOWED",
                exceptionTypeName: typeof(PrincipalChangeNotAllowedException).FullName!,
                category: FailureCategory.Validation,
                description: "Principal cannot be changed after authentication."
            );

        public static readonly ExceptionDefinition SessionExpired =
            ExceptionDefinition.Create(
                code: "SESSIONS.SESSION.EXPIRED",
                exceptionTypeName: typeof(SessionExpiredException).FullName!,
                category: FailureCategory.Internal,
                description: "Session expired by admin or non-idle policy."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                SessionNotFound,
                InvalidPrincipal,
                InvalidTransactionId,
                TransactionAlreadyActive,
                TransactionFinalStateInvalid,
                NegativeTimeout,
                IdleTimeoutExpired,
                InvalidSnapshotLsn,
                SessionLifecycleInvalid,
                PrincipalChangeNotAllowed,
                SessionExpired
            });
        }
    }
}
