using System;
using Silica.BufferPool;
using Silica.Exceptions;

namespace Silica.PageAccess
{
    public sealed class PageTypeMismatchException : SilicaException
    {
        public long FileId { get; }
        public long PageIndex { get; }
        public PageType Expected { get; }
        public PageType Actual { get; }

        public PageTypeMismatchException(in PageId id, PageType expected, PageType actual)
            : base(
                code: PageAccessExceptions.PageTypeMismatch.Code,
                message: $"Page type mismatch at {id.FileId}/{id.PageIndex}: expected={expected}, actual={actual}.",
                category: PageAccessExceptions.PageTypeMismatch.Category,
                exceptionId: PageAccessExceptions.Ids.PageTypeMismatch)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FileId = id.FileId;
            PageIndex = id.PageIndex;
            Expected = expected;
            Actual = actual;
        }
    }
}
