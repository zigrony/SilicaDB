using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class DeviceDisposedException : SilicaException
    {
        public DeviceDisposedException()
            : base(
                code: StorageExceptions.DeviceDisposed.Code,
                message: "Operation attempted on a disposed device.",
                category: StorageExceptions.DeviceDisposed.Category,
                exceptionId: StorageExceptions.Ids.DeviceDisposed)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
