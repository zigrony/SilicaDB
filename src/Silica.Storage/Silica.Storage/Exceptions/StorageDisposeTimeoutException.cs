using System;
using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class StorageDisposeTimeoutException : SilicaException
    {
        public TimeSpan Timeout { get; }

        public StorageDisposeTimeoutException(TimeSpan timeout, Exception? innerException = null)
            : base(
                code: StorageExceptions.StorageDisposeTimeout.Code,
                message: $"Storage device failed to shut down within the configured timeout of {timeout}.",
                category: StorageExceptions.StorageDisposeTimeout.Category,
                exceptionId: StorageExceptions.Ids.StorageDisposeTimeout,
                innerException: innerException)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Timeout = timeout;
        }
    }
}
