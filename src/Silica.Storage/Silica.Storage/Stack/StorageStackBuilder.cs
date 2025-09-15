using System;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;

namespace Silica.Storage.Stack
{
    /// <summary>
    /// Deterministic loader for a storage stack (no reflection).
    /// Builds the chain and programs the base device with the expected manifest.
    /// </summary>
    public static class StorageStackBuilder
    {
        public static IStorageDevice Build(
            IStorageDevice baseDevice,
            MiniDriverSpec[] specs,
            int logicalBlockSize,
            ulong deviceUuidLo,
            ulong deviceUuidHi)
        {
            if (baseDevice is null) throw new ArgumentNullException(nameof(baseDevice));
            if (specs is null) specs = Array.Empty<MiniDriverSpec>();

            // Compose manifest entries for format-affecting drivers only.
            int countFmt = 0;
            for (int i = 0; i < specs.Length; i++)
                if (!specs[i].Passive) countFmt++;

            var entries = countFmt == 0 ? Array.Empty<DeviceManifest.Entry>() : new DeviceManifest.Entry[countFmt];
            int ei = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                if (s.Passive) continue;
                entries[ei++] = new DeviceManifest.Entry(
                    s.Kind, 0, s.Param1, s.Param2, s.Param3, s.Param4);
            }
            var manifest = new DeviceManifest(logicalBlockSize, deviceUuidLo, deviceUuidHi, entries);

            // Provide manifest to the base (must own metadata frame).
            if (baseDevice is IStackManifestHost host)
            {
                host.SetExpectedManifest(manifest);
            }

            // Compose decorators in the specified order.
            IStorageDevice device = baseDevice;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                switch (s.Kind)
                {
                    case MiniDriverKind.Compression:
                        device = new CompressionDevice(device, s.Param1, s.Param2);
                        break;
                    // Future:
                    // case MiniDriverKind.Encryption:
                    //     device = new EncryptionDevice(device, ...);
                    //     break;
                    // case MiniDriverKind.Versioning:
                    //     device = new VersioningDevice(device, ...);
                    //     break;
                    default:
                        // Unknown format-affecting kind => fail fast.
                        if (!s.Passive)
                            throw new InvalidOperationException("Unsupported format-affecting mini-driver kind: " + s.Kind.ToString());
                        // Passive drivers are not part of manifest; allow user-specific wrappers outside this builder.
                        break;
                }
            }
            return device;
        }
    }
}
