using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class DeviceNotMountedException : SilicaException
    {
        public DeviceNotMountedException()
            : base(
                code: StorageExceptions.DeviceNotMounted.Code,
                message: "Operation attempted on a device that is not mounted.",
                category: StorageExceptions.DeviceNotMounted.Category,
                exceptionId: StorageExceptions.Ids.DeviceNotMounted)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
