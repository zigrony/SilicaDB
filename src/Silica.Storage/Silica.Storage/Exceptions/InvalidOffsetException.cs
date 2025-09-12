using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class InvalidOffsetException : SilicaException
    {
        public long Offset { get; }

        public InvalidOffsetException(long offset)
            : base(
                code: StorageExceptions.InvalidOffset.Code,
                message: $"Offset must be non-negative and within device bounds. Offset={offset}",
                category: StorageExceptions.InvalidOffset.Category,
                exceptionId: StorageExceptions.Ids.InvalidOffset)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Offset = offset;
        }
    }
}
