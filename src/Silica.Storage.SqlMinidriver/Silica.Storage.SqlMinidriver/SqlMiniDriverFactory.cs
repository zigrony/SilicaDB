using Silica.Storage;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;
using System.Text.Json;
using System.Text;

namespace Silica.Storage.SqlMinidriver
{
    /// <summary>
    /// Factory for the Sql mini-driver, implementing IMiniDriverFactory.
    /// </summary>
    public sealed class SqlMiniDriverFactory : IMiniDriverFactory
    {
        /// <summary>
        /// The string kind key this factory handles.
        /// </summary>
        public string Kind => "sql";

        /// <summary>
        /// Creates a new SqlDriver wrapping the given inner device.
        /// </summary>
        public IStorageDevice Create(DeviceManifest.Entry entry, IStorageDevice inner)
        {
            var options = ParseSqlOptions(entry);
            return new SqlDriver(inner, options);
        }
        private static SqlOptions ParseSqlOptions(DeviceManifest.Entry entry)
        {
            if (entry.MetadataBlobs != null &&
                entry.MetadataBlobs.TryGetValue("sql", out var blob))
            {
                var json = Encoding.UTF8.GetString(blob);
                return JsonSerializer.Deserialize<SqlOptions>(json)!;
            }

            // fallback defaults
            return new SqlOptions { PageSize = 8192, ReservedSystemPages = 4 };
        }

    }
}
