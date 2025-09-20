using System;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Thrown when a write acquisition specifies an invalid range relative to the page size.
    /// </summary>
    public sealed class InvalidWriteRangeException : SilicaException
    {
        public int Offset { get; }
        public int Length { get; }
        public int PageSize { get; }

        public InvalidWriteRangeException(int offset, int length, int pageSize)
            : base(
                code: BufferPoolExceptions.InvalidWriteRange.Code,
                message: "Invalid write range: offset=" + offset.ToString() + ", length=" + length.ToString() + ", page_size=" + pageSize.ToString() + ".",
                category: BufferPoolExceptions.InvalidWriteRange.Category,
                exceptionId: BufferPoolExceptions.Ids.InvalidWriteRange)
        {
            BufferPoolExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Offset = offset;
            Length = length;
            PageSize = pageSize;
        }
    }
}
