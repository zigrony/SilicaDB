using Silica.Exceptions;

namespace Silica.Storage.Allocation.Exceptions
{
    public sealed class ExtentExhaustedException : SilicaException
    {
        public ExtentExhaustedException(long extentIndex)
            : base(
                code: StorageAllocationExceptions.ExtentExhausted.Code,
                message: $"Extent {extentIndex} has no free pages remaining.",
                category: StorageAllocationExceptions.ExtentExhausted.Category,
                exceptionId: StorageAllocationExceptions.Ids.ExtentExhausted)
        {
            StorageAllocationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ExtentIndex = extentIndex;
        }

        public long ExtentIndex { get; }
    }
}
