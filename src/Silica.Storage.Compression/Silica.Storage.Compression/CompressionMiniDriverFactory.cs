using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;

namespace Silica.Storage.Compression
{
    /// <summary>
    /// Factory for the compression mini-driver, implementing IMiniDriverFactory.
    /// </summary>
    public sealed class CompressionMiniDriverFactory : IMiniDriverFactory
    {
        /// <summary>
        /// The string kind key this factory handles.
        /// </summary>
        public string Kind => "compression";

        /// <summary>
        /// Creates a new CompressionDriver wrapping the given inner device.
        /// </summary>
        public IStorageDevice Create(DeviceManifest.Entry entry, IStorageDevice inner)
        {
            // TODO: parse entry.Parameters into CompressionOptions if needed
            var options = new CompressionOptions();
            return new CompressionDriver(inner, options);
        }
    }
}
