using System;
using System.Collections.Generic;
using Silica.Storage.Interfaces;
using Silica.Storage.Encryption;
using Silica.Storage.MiniDrivers;
using Silica.DiagnosticsCore; 
using Silica.DiagnosticsCore.Tracing;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Storage.Stack
{
    /// <summary>
    /// Deterministic loader for a storage stack (no reflection).
    /// Builds the chain and programs the base device with the expected manifest.
    /// </summary>
    public static class StorageStackBuilder
    {
        private static void ValidateManifestFitsOrThrow(in DeviceManifest manifest, int logicalBlockSize)
        {
            if (logicalBlockSize <= 0)
                throw new InvalidOperationException("LogicalBlockSize must be > 0 to validate manifest fit.");

            var buf = new byte[logicalBlockSize];
            if (!manifest.TrySerialize(buf))
            {
                throw new InvalidOperationException(
                    "Device manifest does not fit in the metadata frame. " +
                    "Reduce format-affecting entries and/or per-entry metadata blobs, or increase LogicalBlockSize.");
            }
        }

        /// <summary>
        /// Build a storage stack deterministically from the given base device and specs.
        /// This overload does not compose encryption (requires out-of-band secrets).
        /// </summary>
        public static IStorageDevice Build(
            IStorageDevice baseDevice,
            MiniDriverSpec[] specs,
            int logicalBlockSize,
            ulong deviceUuidLo,
            ulong deviceUuidHi)
        {
            if (baseDevice is null) throw new ArgumentNullException(nameof(baseDevice));
            if (specs is null) specs = Array.Empty<MiniDriverSpec>();
            if (baseDevice.Geometry.LogicalBlockSize != logicalBlockSize)
                throw new InvalidOperationException("Builder logicalBlockSize must match baseDevice.Geometry.LogicalBlockSize.");

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
                var blobs = s.MetadataBlobs is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal) : new Dictionary<string, byte[]>(s.MetadataBlobs, StringComparer.Ordinal);
                // Persist string kind in blob directory for on-disk manifests
                blobs["driver.kind"] = System.Text.Encoding.UTF8.GetBytes(s.Kind ?? string.Empty);
                entries[ei++] = new DeviceManifest.Entry(
                    kind: s.Kind,
                    flags: 0,
                    p1: s.Param1,
                    p2: s.Param2,
                    p3: s.Param3,
                    p4: s.Param4,
                    blobs: blobs);
            }
            var manifest = new DeviceManifest(logicalBlockSize, deviceUuidLo, deviceUuidHi, entries);

            ValidateManifestFitsOrThrow(manifest, logicalBlockSize);
            if (baseDevice is IStackManifestHost host)
                host.SetExpectedManifest(manifest);
            else
                throw new InvalidOperationException("Base device does not host the stack manifest (IStackManifestHost required).");

            // Compose decorators deterministically from the manifest entries via the registry.
            IStorageDevice device = baseDevice;
            if (manifest.Entries is { Length: > 0 })
            {
                foreach (var entry in manifest.Entries)
                {
                    if (MiniDriverRegistry.TryCreate(entry, device, out var decorated) && decorated is not null)
                    {
                        device = decorated;
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Unsupported format-affecting mini-driver kind: '{entry.Kind}'");
                }
            }
            return device;
        }

        /// <summary>
        /// Build with encryption composition. Keys/config are supplied via factories from MiniDriverSpec.
        /// This preserves manifest semantics (format-affecting entry) while keeping secrets out-of-band.
        /// </summary>
        public static IStorageDevice Build(
            IStorageDevice baseDevice,
            MiniDriverSpec[] specs,
            int logicalBlockSize,
            ulong deviceUuidLo,
            ulong deviceUuidHi,
            Func<MiniDriverSpec, IEncryptionKeyProvider> encryptionKeyFactory,
            Func<MiniDriverSpec, CounterDerivationConfig>? encryptionConfigFactory = null)
        {
            if (encryptionKeyFactory is null) throw new ArgumentNullException(nameof(encryptionKeyFactory));
            if (baseDevice is null) throw new ArgumentNullException(nameof(baseDevice));
            if (specs is null) specs = Array.Empty<MiniDriverSpec>();
            if (baseDevice.Geometry.LogicalBlockSize != logicalBlockSize)
                throw new InvalidOperationException("Builder logicalBlockSize must match baseDevice.Geometry.LogicalBlockSize.");

            // Build manifest entries
            int countFmt = 0;
            for (int i = 0; i < specs.Length; i++)
                if (!specs[i].Passive) countFmt++;

            var entries = countFmt == 0 ? Array.Empty<DeviceManifest.Entry>() : new DeviceManifest.Entry[countFmt];
            int ei = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                if (s.Passive) continue;

                Dictionary<string, byte[]>? blobs = s.MetadataBlobs;
                if (string.Equals(s.Kind, "encryption", StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = encryptionConfigFactory is null
                        ? new CounterDerivationConfig(CounterDerivationKind.SaltXorFrameId_V1, 1, 0, 0)
                        : encryptionConfigFactory(s);
                    var blobBytes = CounterDerivationConfig.Serialize(cfg);
                    blobs ??= new Dictionary<string, byte[]>(1, StringComparer.Ordinal);
                    blobs[CounterDerivationConfig.BlobKey] = blobBytes;
                    // Also persist the string kind
                    blobs["driver.kind"] = System.Text.Encoding.UTF8.GetBytes(s.Kind ?? string.Empty);
                }

                entries[ei++] = new DeviceManifest.Entry(
                    kind: s.Kind,
                    flags: 0,
                    p1: s.Param1,
                    p2: s.Param2,
                    p3: s.Param3,
                    p4: s.Param4,
                    blobs: blobs);
            }
            var manifest = new DeviceManifest(logicalBlockSize, deviceUuidLo, deviceUuidHi, entries);

            ValidateManifestFitsOrThrow(manifest, logicalBlockSize);
            if (baseDevice is IStackManifestHost host)
                host.SetExpectedManifest(manifest);
            else
                throw new InvalidOperationException("Base device does not host the stack manifest (IStackManifestHost required).");

            // Compose decorators, with explicit support for Encryption.
            IStorageDevice device = baseDevice;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                switch (s.Kind)
                {
                    case "encryption":
                        if (!s.Passive)
                        {
                            var keys = encryptionKeyFactory(s);
                            if (keys is null)
                                throw new InvalidOperationException("Encryption key factory returned null.");

                            var cfg = encryptionConfigFactory is null
                                ? new CounterDerivationConfig(CounterDerivationKind.SaltXorFrameId_V1, 1, 0, 0)
                                : encryptionConfigFactory(s);

                            device = new EncryptionDevice(device, keys, cfg);
                        }
                        break;

                    default:
                        if (!s.Passive)
                            throw new InvalidOperationException(
                                "Unsupported format-affecting mini-driver kind: " + (s.Kind ?? string.Empty));
                        break;
                }
            }
            return device;
        }
    }
}
