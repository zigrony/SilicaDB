using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class InvalidGeometryException : SilicaException
    {
        public string Details { get; }

        public InvalidGeometryException(string details)
            : base(
                code: StorageExceptions.InvalidGeometry.Code,
                message: $"Device geometry is invalid or inconsistent. {details}",
                category: StorageExceptions.InvalidGeometry.Category,
                exceptionId: StorageExceptions.Ids.InvalidGeometry)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Details = details;
        }
    }
}
