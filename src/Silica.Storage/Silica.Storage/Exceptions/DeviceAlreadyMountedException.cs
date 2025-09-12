using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class DeviceAlreadyMountedException : SilicaException
    {
        public DeviceAlreadyMountedException()
            : base(
                code: StorageExceptions.DeviceAlreadyMounted.Code,
                message: "Operation attempted on a device that is already mounted.",
                category: StorageExceptions.DeviceAlreadyMounted.Category,
                exceptionId: StorageExceptions.Ids.DeviceAlreadyMounted)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
