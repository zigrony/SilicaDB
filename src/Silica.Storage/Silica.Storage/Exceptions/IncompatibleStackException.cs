using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class IncompatibleStackException : SilicaException
    {
        public IncompatibleStackException(string details)
            : base(
                code: StorageExceptions.InvalidGeometry.Code,
                message: "Device stack manifest is incompatible. " + (details ?? string.Empty),
                category: StorageExceptions.InvalidGeometry.Category,
                exceptionId: StorageExceptions.Ids.InvalidGeometry)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
