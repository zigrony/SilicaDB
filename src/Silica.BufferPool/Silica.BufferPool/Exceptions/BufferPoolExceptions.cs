using System;
using System.Collections.Generic;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Canonical, low-cardinality BufferPool exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class BufferPoolExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for buffer pool: 2000–2999
            public const int InvalidWriteRange = 2001;
            public const int UnbalancedPin = 2002;
            public const int LatchReaderUnderflow = 2003;
            public const int PageSizeTooLargeForWalPayload = 2004;
            public const int BufferPoolDisposed = 2005;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.BufferPool reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    "ExceptionId must be between " + MinId.ToString() + " and " + MaxId.ToString() + " for Silica.BufferPool.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------
        public static readonly ExceptionDefinition InvalidWriteRange =
            ExceptionDefinition.Create(
                code: "BUFFERPOOL.WRITE_RANGE.INVALID",
                exceptionTypeName: typeof(InvalidWriteRangeException).FullName!,
                category: FailureCategory.Validation,
                description: "Invalid write range for page: offset/length violate page bounds.");

        public static readonly ExceptionDefinition UnbalancedPin =
            ExceptionDefinition.Create(
                code: "BUFFERPOOL.PIN.UNBALANCED",
                exceptionTypeName: typeof(UnbalancedPinException).FullName!,
                category: FailureCategory.Internal,
                description: "Unbalanced pin/unpin detected on a page frame.");

        public static readonly ExceptionDefinition LatchReaderUnderflow =
            ExceptionDefinition.Create(
                code: "BUFFERPOOL.LATCH.READER_UNDERFLOW",
                exceptionTypeName: typeof(LatchReaderUnderflowException).FullName!,
                category: FailureCategory.Internal,
                description: "Reader latch underflow detected during release.");

        public static readonly ExceptionDefinition PageSizeTooLargeForWalPayload =
            ExceptionDefinition.Create(
                code: "BUFFERPOOL.WAL.PAGESIZE_TOO_LARGE",
                exceptionTypeName: typeof(PageSizeTooLargeForWalPayloadException).FullName!,
                category: FailureCategory.Validation,
                description: "Page size too large to encode a full-page WAL payload.");

        public static readonly ExceptionDefinition BufferPoolDisposed =
            ExceptionDefinition.Create(
                code: "BUFFERPOOL.MANAGER.DISPOSED",
                exceptionTypeName: typeof(BufferPoolDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed BufferPoolManager.");

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                InvalidWriteRange,
                UnbalancedPin,
                LatchReaderUnderflow,
                PageSizeTooLargeForWalPayload,
                BufferPoolDisposed
            });
        }
    }
}
