using System.Collections.Generic;

namespace Silica.Storage.MiniDrivers
{
    /// <summary>
    /// Declarative mini-driver spec for the loader (no reflection).
    /// Passive drivers are not written to the device manifest.
    /// </summary>
    public readonly struct MiniDriverSpec
    {
        public readonly byte Kind;         // see MiniDriverKind
        public readonly uint Param1;       // interpretation depends on Kind
        public readonly uint Param2;
        public readonly uint Param3;
        public readonly uint Param4;
        public readonly bool Passive;      // telemetry, latency, fault-injection, etc.
        // Optional: driver-owned named blobs persisted in manifest (v2)
        public readonly Dictionary<string, byte[]>? MetadataBlobs;

        public MiniDriverSpec(
            byte kind,
            uint p1 = 0,
            uint p2 = 0,
            uint p3 = 0,
            uint p4 = 0,
            bool passive = false,
            Dictionary<string, byte[]>? blobs = null)
        {
            Kind = kind;
            Param1 = p1;
            Param2 = p2;
            Param3 = p3;
            Param4 = p4;
            Passive = passive;
            MetadataBlobs = blobs;
        }
    }
}
