using System;
using Silica.BufferPool;
using Silica.Exceptions;
using Silica.Common.Primitives;

namespace Silica.PageAccess
{
    public sealed class PageVersionMismatchException : SilicaException
    {
        public long FileId { get; }
        public long PageIndex { get; }
        public PageVersion Expected { get; }
        public PageVersion Actual { get; }
        public string Reason { get; }

        public PageVersionMismatchException(in PageId id, PageVersion expected, PageVersion actual, string reason)
            : base(
                code: PageAccessExceptions.PageVersionMismatch.Code,
                message: $"Page version mismatch at {id.FileId}/{id.PageIndex}: expected={expected}, actual={actual} ({reason}).",
                category: PageAccessExceptions.PageVersionMismatch.Category,
                exceptionId: PageAccessExceptions.Ids.PageVersionMismatch)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FileId = id.FileId;
            PageIndex = id.PageIndex;
            Expected = expected;
            Actual = actual;
            Reason = reason ?? string.Empty;
        }
    }
}
