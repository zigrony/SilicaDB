// File: Silica.FrontEnds/Exceptions/FrontendExceptions.cs
using System;
using Silica.Exceptions;

namespace Silica.FrontEnds.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality FrontEnds exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class FrontendExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for frontends: 3000–3999
            public const int FrontendBindFailed = 3001;
            public const int CertificateAcquireFailed = 3002;
            public const int FrontendConfigurationFailed = 3003;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.FrontEnds.
        /// </summary>
        public const int MinId = 3000;
        public const int MaxId = 3999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.FrontEnds reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.FrontEnds.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition FrontendBindFailed =
            ExceptionDefinition.Create(
                code: "FRONTENDS.BIND.FAILED",
                exceptionTypeName: typeof(FrontendBindFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Frontend failed to bind (e.g., port/protocol/TLS initialization)."
            );

        public static readonly ExceptionDefinition CertificateAcquireFailed =
            ExceptionDefinition.Create(
                code: "FRONTENDS.CERTIFICATE.ACQUIRE_FAILED",
                exceptionTypeName: typeof(CertificateAcquireFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to acquire server certificate required for TLS bind."
            );

        public static readonly ExceptionDefinition FrontendConfigurationFailed =
            ExceptionDefinition.Create(
                code: "FRONTENDS.CONFIGURE.FAILED",
                exceptionTypeName: typeof(FrontendConfigurationFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Frontend configuration failed during registry orchestration."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                FrontendBindFailed,
                CertificateAcquireFailed,
                FrontendConfigurationFailed
            });
        }
    }
}
