using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class ShortReadException : SilicaException
    {
        public int ExpectedBytes { get; }
        public int ActualBytes { get; }

        public ShortReadException(int expectedBytes, int actualBytes)
            : base(
                code: StorageExceptions.ShortRead.Code,
                message: $"Underlying stream returned fewer bytes than requested. Expected={expectedBytes}, Actual={actualBytes}",
                category: StorageExceptions.ShortRead.Category,
                exceptionId: StorageExceptions.Ids.ShortRead)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ExpectedBytes = expectedBytes;
            ActualBytes = actualBytes;
        }
    }
}
