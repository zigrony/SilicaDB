using System;
using Silica.Exceptions;

namespace Silica.PageAccess
{
    /// <summary>
    /// Canonical, low-cardinality PageAccess exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class PageAccessExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS (PageAccess: 2000–2999)
        // --------------------------
        public static class Ids
        {
            public const int PageHeaderInvalid = 2001;
            public const int MagicMismatch = 2002;
            public const int PageTypeMismatch = 2003;
            public const int PageVersionMismatch = 2004;
            public const int PageChecksumMismatch = 2005;
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
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.PageAccess.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------
        public static readonly ExceptionDefinition PageHeaderInvalid =
            ExceptionDefinition.Create(
                code: "PAGEACCESS.HEADER.INVALID",
                exceptionTypeName: typeof(PageHeaderInvalidException).FullName!,
                category: FailureCategory.Internal,
                description: "Invalid page header or physical invariant violation."
            );

        public static readonly ExceptionDefinition MagicMismatch =
            ExceptionDefinition.Create(
                code: "PAGEACCESS.HEADER.MAGIC_MISMATCH",
                exceptionTypeName: typeof(MagicMismatchException).FullName!,
                category: FailureCategory.Validation,
                description: "Magic number mismatch for page header."
            );

        public static readonly ExceptionDefinition PageTypeMismatch =
            ExceptionDefinition.Create(
                code: "PAGEACCESS.HEADER.TYPE_MISMATCH",
                exceptionTypeName: typeof(PageTypeMismatchException).FullName!,
                category: FailureCategory.Validation,
                description: "Page type mismatch for page header."
            );

        public static readonly ExceptionDefinition PageVersionMismatch =
            ExceptionDefinition.Create(
                code: "PAGEACCESS.HEADER.VERSION_MISMATCH",
                exceptionTypeName: typeof(PageVersionMismatchException).FullName!,
                category: FailureCategory.Validation,
                description: "Page version mismatch for page header."
            );

        public static readonly ExceptionDefinition PageChecksumMismatch =
            ExceptionDefinition.Create(
                code: "PAGEACCESS.HEADER.CHECKSUM_MISMATCH",
                exceptionTypeName: typeof(PageChecksumMismatchException).FullName!,
                category: FailureCategory.Validation,
                description: "Checksum mismatch detected for page."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                PageHeaderInvalid,
                MagicMismatch,
                PageTypeMismatch,
                PageVersionMismatch,
                PageChecksumMismatch
            });
        }
    }
}
