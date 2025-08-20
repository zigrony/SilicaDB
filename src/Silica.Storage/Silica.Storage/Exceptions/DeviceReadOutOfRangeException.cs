namespace Silica.Storage.Exceptions
{
    /// <summary>
    /// Thrown when a read request extends past the device's current length.
    /// </summary>
    public sealed class DeviceReadOutOfRangeException : IOException
    {
        public long Offset { get; }
        public int RequestedLength { get; }
        public long DeviceLength { get; }

        public DeviceReadOutOfRangeException(long offset, int requestedLength, long deviceLength)
            : base($"Read beyond EOF: offset={offset}, length={requestedLength}, deviceLength={deviceLength}")
        {
            Offset = offset;
            RequestedLength = requestedLength;
            DeviceLength = deviceLength;
        }
    }
}
