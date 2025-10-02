// File: Silica.Storage/MiniDrivers/MiniDriverRegistry.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.Storage.Interfaces;

namespace Silica.Storage.MiniDrivers
{
    /// <summary>
    /// Deterministic, runtime registry for mini-driver factories (no reflection).
    /// Allows StorageStackBuilder to compose the stack directly from manifest entries.
    /// </summary>
    public static class MiniDriverRegistry
    {
        private static readonly Dictionary<string, IMiniDriverFactory> s_factories = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register a factory for a specific driver kind (string key).
        /// Subsequent registrations for the same kind overwrite the prior entry.
        /// </summary>
        public static void Register(IMiniDriverFactory factory)
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            s_factories[factory.Kind] = factory;
        }

        /// <summary>
        /// Compose a decorated device for the given manifest entry layered on top of 'inner'.
        /// Throws when no factory is registered for entry.Kind.
        /// </summary>
        public static IStorageDevice Create(DeviceManifest.Entry entry, IStorageDevice inner)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            if (s_factories.TryGetValue(entry.Kind ?? string.Empty, out var f))
                return f.Create(entry, inner);

            throw new InvalidOperationException($"No factory registered for driver kind='{entry.Kind ?? string.Empty}'");
        }

        /// <summary>
        /// Try variant to allow graceful fallback when a kind is reserved or not implemented.
        /// </summary>
        public static bool TryCreate(DeviceManifest.Entry entry, IStorageDevice inner, out IStorageDevice? device)
        {
            device = null;
            if (inner is null) return false;
            if (!s_factories.TryGetValue(entry.Kind ?? string.Empty, out var f)) return false;
            device = f.Create(entry, inner);
            return true;
        }
    }
}
