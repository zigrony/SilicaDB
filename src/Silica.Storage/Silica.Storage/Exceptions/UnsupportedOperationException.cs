using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class UnsupportedOperationException : SilicaException
    {
        public string OperationName { get; }

        public UnsupportedOperationException(string operationName)
            : base(
                code: StorageExceptions.UnsupportedOperation.Code,
                message: $"Operation '{operationName}' is not supported by this device or stream configuration.",
                category: StorageExceptions.UnsupportedOperation.Category,
                exceptionId: StorageExceptions.Ids.UnsupportedOperation)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            OperationName = operationName;
        }
    }
}
