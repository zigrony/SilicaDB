// Filename: Silica.DiagnosticsCore/Exceptions/DiagnosticsCoreExceptions.cs
using System;
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Canonical, low-cardinality DiagnosticsCore exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class DiagnosticsCoreExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for DiagnosticsCore: 2000–2999
            public const int DispatcherFullModeUnsupported = 2001;
            public const int SinkInitializationFailed = 2002;
            public const int TraceRedactionRequired = 2003;
            public const int MetricsRegistrationConflict = 2004;
            public const int ShutdownTimeout = 2005;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.DiagnosticsCore.
        /// </summary>
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.DiagnosticsCore reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.DiagnosticsCore.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition DispatcherFullModeUnsupported =
            ExceptionDefinition.Create(
                code: "DIAGCORE.DISPATCHER.FULLMODE_UNSUPPORTED",
                exceptionTypeName: typeof(DispatcherFullModeUnsupportedException).FullName!,
                category: FailureCategory.Validation,
                description: "DispatcherFullMode is unsupported and cannot be coerced under strict bootstrap options."
            );

        public static readonly ExceptionDefinition SinkInitializationFailed =
            ExceptionDefinition.Create(
                code: "DIAGCORE.SINK.INIT_FAILED",
                exceptionTypeName: typeof(SinkInitializationFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Trace sink failed to initialize."
            );

        public static readonly ExceptionDefinition TraceRedactionRequired =
            ExceptionDefinition.Create(
                code: "DIAGCORE.TRACE.REDACTION_REQUIRED",
                exceptionTypeName: typeof(TraceRedactionRequiredException).FullName!,
                category: FailureCategory.Validation,
                description: "Trace emission rejected because redaction is required but the event is not sanitized."
            );

        public static readonly ExceptionDefinition MetricsRegistrationConflict =
            ExceptionDefinition.Create(
                code: "DIAGCORE.METRICS.REGISTRATION_CONFLICT",
                exceptionTypeName: typeof(MetricsRegistrationConflictException).FullName!,
                category: FailureCategory.Internal,
                description: "Metric registration conflicted with an existing schema."
            );

        public static readonly ExceptionDefinition ShutdownTimeout =
            ExceptionDefinition.Create(
                code: "DIAGCORE.DISPATCHER.SHUTDOWN_TIMEOUT",
                exceptionTypeName: typeof(ShutdownTimeoutException).FullName!,
                category: FailureCategory.Internal,
                description: "Dispatcher shutdown exceeded timeout with backlog remaining."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                DispatcherFullModeUnsupported,
                SinkInitializationFailed,
                TraceRedactionRequired,
                MetricsRegistrationConflict,
                ShutdownTimeout
            });
        }
    }
}
