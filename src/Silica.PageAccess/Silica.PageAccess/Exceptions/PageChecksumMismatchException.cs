using System;
using Silica.BufferPool;
using Silica.Exceptions;

namespace Silica.PageAccess
{
    public sealed class PageChecksumMismatchException : SilicaException
    {
        public long FileId { get; }
        public long PageIndex { get; }
        public uint Expected { get; }
        public uint Actual { get; }

        public PageChecksumMismatchException(in PageId id, uint expected, uint actual)
            : base(
                code: PageAccessExceptions.PageChecksumMismatch.Code,
                message: $"Checksum mismatch at {id.FileId}/{id.PageIndex}: header={expected:X8}, computed={actual:X8}.",
                category: PageAccessExceptions.PageChecksumMismatch.Category,
                exceptionId: PageAccessExceptions.Ids.PageChecksumMismatch)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FileId = id.FileId;
            PageIndex = id.PageIndex;
            Expected = expected;
            Actual = actual;
        }
    }
}
