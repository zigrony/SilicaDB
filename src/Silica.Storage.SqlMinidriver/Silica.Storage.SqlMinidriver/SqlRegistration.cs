using Silica.Storage.MiniDrivers;

namespace Silica.Storage.SqlMinidriver
{
    /// <summary>
    /// Static entry point for registering the Sql mini-driver
    /// with the Silica.Storage MiniDriverRegistry.
    /// </summary>
    public static class SqlRegistration
    {
        /// <summary>
        /// Registers the SqlMiniDriverFactory with the static MiniDriverRegistry.
        /// Call this once at application/plugin startup.
        /// </summary>
        public static void Register()
        {
            MiniDriverRegistry.Register(new SqlMiniDriverFactory());
        }
    }
}
