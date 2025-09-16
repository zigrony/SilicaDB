using System;
using System.Collections.Generic;
using Silica.Storage.Interfaces;
using Silica.Storage.MiniDrivers;
using Silica.Storage.Encryption;

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
            // Serialize into an exact-size buffer to validate capacity deterministically.
            // We intentionally avoid heap churn and any dynamic sizing beyond the exact frame.
            if (logicalBlockSize <= 0)
                throw new InvalidOperationException("LogicalBlockSize must be > 0 to validate manifest fit.");

            var buf = new byte[logicalBlockSize];
            if (!manifest.TrySerialize(buf))
            {
                // Provide explicit guidance to the caller with componentized detail
                // rather than deferring to mount-time failures.
                throw new InvalidOperationException(
                    "Device manifest does not fit in the metadata frame. " +
                    "Reduce format-affecting entries and/or per-entry metadata blobs, or increase LogicalBlockSize.");
            }
        }

        public static IStorageDevice Build(
            IStorageDevice baseDevice,
            MiniDriverSpec[] specs,
            int logicalBlockSize,
            ulong deviceUuidLo,
            ulong deviceUuidHi)
        {
            if (baseDevice is null) throw new ArgumentNullException(nameof(baseDevice));
            if (specs is null) specs = Array.Empty<MiniDriverSpec>();
            // Enforce geometry/manifest consistency up front for crisp diagnostics.
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
                entries[ei++] = new DeviceManifest.Entry(
                    kind: s.Kind,
                    flags: 0,
                    p1: s.Param1,
                    p2: s.Param2,
                    p3: s.Param3,
                    p4: s.Param4,
                    blobs: s.MetadataBlobs);
            }
            var manifest = new DeviceManifest(logicalBlockSize, deviceUuidLo, deviceUuidHi, entries);

            // Always program the manifest, even when there are zero format-affecting entries.
            // This ensures deterministic frame 0 initialization on first mount.
            ValidateManifestFitsOrThrow(manifest, logicalBlockSize);
            if (baseDevice is IStackManifestHost host)
            {
                host.SetExpectedManifest(manifest);
            }
            else
            {
                throw new InvalidOperationException("Base device does not host the stack manifest (IStackManifestHost required).");
            }

            // Compose decorators in the specified order.
            IStorageDevice device = baseDevice;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                switch (s.Kind)
                {
                    case MiniDriverKind.Compression:
                        if (!s.Passive)
                            device = new CompressionDevice(device, s.Param1, s.Param2);
                        break;
                    // Encryption handled by the overload below (requires out-of-band keys/config).
                    // This default Build overload intentionally does not compose Encryption to avoid manifest-coupled secrets.
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

            // First, program the manifest exactly as in the baseline overload.
            int countFmt = 0;
            for (int i = 0; i < specs.Length; i++)
                if (!specs[i].Passive) countFmt++;

            var entries = countFmt == 0 ? Array.Empty<DeviceManifest.Entry>() : new DeviceManifest.Entry[countFmt];
            int ei = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                if (s.Passive) continue;
                // Persist encryption counter config deterministically in the manifest blobs
                Dictionary<string, byte[]>? blobs = s.MetadataBlobs;
                if (s.Kind == MiniDriverKind.Encryption)
                {
                    var cfg = encryptionConfigFactory is null
                        ? new CounterDerivationConfig(CounterDerivationKind.SaltXorFrameId_V1, 1, 0, 0)
                        : encryptionConfigFactory(s);
                    var blobBytes = CounterDerivationConfig.Serialize(cfg);
                    if (blobs is null)
                    {
                        blobs = new Dictionary<string, byte[]>(1, StringComparer.Ordinal);
                    }
                    // Overwrite any existing key to enforce single source of truth.
                    blobs[CounterDerivationConfig.BlobKey] = blobBytes;
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
            // Always program the manifest, even for zero-entry stacks.
            ValidateManifestFitsOrThrow(manifest, logicalBlockSize);
            if (baseDevice is IStackManifestHost host)
                host.SetExpectedManifest(manifest);
            else
                throw new InvalidOperationException("Base device does not host the stack manifest (IStackManifestHost required).");

            // Then compose decorators, with explicit support for Encryption.
            IStorageDevice device = baseDevice;
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                switch (s.Kind)
                {
                    case MiniDriverKind.Compression:
                        if (!s.Passive)
                            device = new CompressionDevice(device, s.Param1, s.Param2);
                        break;
                    case MiniDriverKind.Encryption:
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
                            throw new InvalidOperationException("Unsupported format-affecting mini-driver kind: " + s.Kind.ToString());
                        break;
                }
            }
            return device;
        }
    }
}
