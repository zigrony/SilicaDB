// File: Silica.Storage/MiniDrivers/IMiniDriverFactory.cs
using Silica.Storage.Interfaces;

namespace Silica.Storage.MiniDrivers
{
    /// <summary>
    /// Factory contract for composing a format-affecting mini-driver
    /// (decorator) from a manifest entry and an inner device.
    /// </summary>
    public interface IMiniDriverFactory
    {
        /// <summary>
        /// String kind key (DeviceManifest.Entry.Kind) this factory handles (e.g., "compression").
        /// </summary>
        string Kind { get; }

        /// <summary>
        /// Create a decorated device for the given manifest entry layered on top of 'inner'.
        /// Implementations should not mutate the manifest entry; they only interpret it.
        /// </summary>
        IStorageDevice Create(DeviceManifest.Entry entry, IStorageDevice inner);
    }
}
