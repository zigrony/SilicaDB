using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    /// <summary>
    /// Thrown when a read request extends past the device's current length.
    /// </summary>
    public sealed class DeviceReadOutOfRangeException : SilicaException
    {
        public long Offset { get; }
        public int RequestedLength { get; }
        public long DeviceLength { get; }

        public DeviceReadOutOfRangeException(long offset, int requestedLength, long deviceLength)
            : base(
                code: StorageExceptions.DeviceReadOutOfRange.Code,
                message: $"Read beyond EOF: offset={offset}, length={requestedLength}, deviceLength={deviceLength}",
                category: StorageExceptions.DeviceReadOutOfRange.Category,
                exceptionId: StorageExceptions.Ids.DeviceReadOutOfRange)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Offset = offset;
            RequestedLength = requestedLength;
            DeviceLength = deviceLength;
        }
    }
}
