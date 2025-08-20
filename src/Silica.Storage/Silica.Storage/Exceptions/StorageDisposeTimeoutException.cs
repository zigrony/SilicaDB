namespace Silica.Storage.Exceptions
{
    /// <summary>
    /// Thrown when a storage device fails to shut down within the configured timeout.
    /// </summary>
    public class StorageDisposeTimeoutException : TimeoutException
    {
        public string DeviceName { get; }
        public TimeSpan Timeout { get; }

        public StorageDisposeTimeoutException(string deviceName, TimeSpan timeout)
            : base($"Dispose timed out after {timeout} on device '{deviceName}'.")
        {
            DeviceName = deviceName;
            Timeout = timeout;
        }
    }
}
