using Silica.Storage.MiniDrivers;

namespace Silica.Storage.Compression
{
    /// <summary>
    /// Static entry point for registering the compression mini-driver
    /// with the Silica.Storage MiniDriverRegistry.
    /// </summary>
    public static class CompressionRegistration
    {
        /// <summary>
        /// Registers the CompressionMiniDriverFactory with the static MiniDriverRegistry.
        /// Call this once at application/plugin startup.
        /// </summary>
        public static void Register()
        {
            MiniDriverRegistry.Register(new CompressionMiniDriverFactory());
        }
    }
}
