using Silica.Exceptions;
using Silica.BufferPool;
using Silica.Common.Primitives;

namespace Silica.Storage.Allocation.Exceptions
{
    public sealed class PageFreeException : SilicaException
    {
        public PageFreeException(PageId pageId)
            : base(
                code: StorageAllocationExceptions.PageFreeInvalid.Code,
                message: $"Page {pageId} is already free or cannot be freed.",
                category: StorageAllocationExceptions.PageFreeInvalid.Category,
                exceptionId: StorageAllocationExceptions.Ids.PageFreeInvalid)
        {
            StorageAllocationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            PageId = pageId;
        }

        public PageId PageId { get; }
    }
}
