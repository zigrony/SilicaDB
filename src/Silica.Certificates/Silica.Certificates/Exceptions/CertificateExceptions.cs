// Filename: Silica.Certificates/Exceptions/CertificateExceptions.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    /// <summary>
    /// Canonical, low-cardinality Certificate exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class CertificateExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for certificates: 2000–2999
            public const int GenerationFailed = 2001;
            public const int LoadFailed = 2002;
            public const int ExportFailed = 2003;
            public const int BindFailed = 2004;
            public const int RenewalFailed = 2005;
            public const int InvalidCertificateFormat = 2006;
            public const int CertificateExpired = 2007;
            public const int CertificateNotFound = 2008;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Certificates.
        /// </summary>
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Certificates reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Certificates.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------
        public static readonly ExceptionDefinition GenerationFailed =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.GENERATE.FAILED",
                exceptionTypeName: typeof(CertificateGenerationFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to generate certificate."
            );

        public static readonly ExceptionDefinition LoadFailed =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.LOAD.FAILED",
                exceptionTypeName: typeof(CertificateLoadFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to load certificate from the specified source."
            );

        public static readonly ExceptionDefinition ExportFailed =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.EXPORT.FAILED",
                exceptionTypeName: typeof(CertificateExportFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to export certificate to the requested format."
            );

        public static readonly ExceptionDefinition BindFailed =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.BIND.FAILED",
                exceptionTypeName: typeof(CertificateBindFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to bind certificate to an endpoint."
            );

        public static readonly ExceptionDefinition RenewalFailed =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.RENEW.FAILED",
                exceptionTypeName: typeof(CertificateRenewalFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "Failed to renew/rotate certificate."
            );

        public static readonly ExceptionDefinition InvalidCertificateFormat =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.FORMAT.INVALID",
                exceptionTypeName: typeof(InvalidCertificateFormatException).FullName!,
                category: FailureCategory.Validation,
                description: "Unsupported or invalid certificate format."
            );

        public static readonly ExceptionDefinition CertificateExpired =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.VALIDITY.EXPIRED",
                exceptionTypeName: typeof(CertificateExpiredException).FullName!,
                category: FailureCategory.Validation,
                description: "Certificate is expired or outside its validity window."
            );

        public static readonly ExceptionDefinition CertificateNotFound =
            ExceptionDefinition.Create(
                code: "CERTIFICATE.SOURCE.NOT_FOUND",
                exceptionTypeName: typeof(CertificateNotFoundException).FullName!,
                category: FailureCategory.Validation,
                description: "Certificate not found at the specified source."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                GenerationFailed,
                LoadFailed,
                ExportFailed,
                BindFailed,
                RenewalFailed,
                InvalidCertificateFormat,
                CertificateExpired,
                CertificateNotFound
            });
        }
    }
}
