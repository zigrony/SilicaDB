using Silica.Exceptions;

namespace Silica.Storage.Allocation.Exceptions
{
    public sealed class AllocationFailedException : SilicaException
    {
        public AllocationFailedException(string reason)
            : base(
                code: StorageAllocationExceptions.AllocationFailed.Code,
                message: reason,
                category: StorageAllocationExceptions.AllocationFailed.Category,
                exceptionId: StorageAllocationExceptions.Ids.AllocationFailed)
        {
            StorageAllocationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
