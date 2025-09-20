using System;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Thrown when the page size is too large to allocate a full-page WAL payload buffer.
    /// </summary>
    public sealed class PageSizeTooLargeForWalPayloadException : SilicaException
    {
        public int PageSize { get; }

        public PageSizeTooLargeForWalPayloadException(int pageSize)
            : base(
                code: BufferPoolExceptions.PageSizeTooLargeForWalPayload.Code,
                message: "Page size too large for WAL payload: page_size=" + pageSize.ToString() + ".",
                category: BufferPoolExceptions.PageSizeTooLargeForWalPayload.Category,
                exceptionId: BufferPoolExceptions.Ids.PageSizeTooLargeForWalPayload)
        {
            BufferPoolExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            PageSize = pageSize;
        }
    }
}
