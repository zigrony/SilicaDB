using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class InvalidLengthException : SilicaException
    {
        public int Length { get; }

        public InvalidLengthException(int length)
            : base(
                code: StorageExceptions.InvalidLength.Code,
                message: $"Length must be positive and within device bounds. Length={length}",
                category: StorageExceptions.InvalidLength.Category,
                exceptionId: StorageExceptions.Ids.InvalidLength)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Length = length;
        }
    }
}
