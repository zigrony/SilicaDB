using System;
using Silica.BufferPool;
using Silica.Exceptions;

namespace Silica.PageAccess
{
    public sealed class MagicMismatchException : SilicaException
    {
        public long FileId { get; }
        public long PageIndex { get; }
        public uint Expected { get; }
        public uint Actual { get; }

        public MagicMismatchException(in PageId id, uint expected, uint actual)
            : base(
                code: PageAccessExceptions.MagicMismatch.Code,
                message: $"Magic mismatch at {id.FileId}/{id.PageIndex}: expected={expected:X8}, actual={actual:X8}.",
                category: PageAccessExceptions.MagicMismatch.Category,
                exceptionId: PageAccessExceptions.Ids.MagicMismatch)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FileId = id.FileId;
            PageIndex = id.PageIndex;
            Expected = expected;
            Actual = actual;
        }
    }
}
