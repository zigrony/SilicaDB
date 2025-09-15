using System;
using System.Buffers.Binary;

namespace Silica.Storage
{
    /// <summary>
    /// Deterministic, binary manifest written to frame 0.
    /// Encodes geometry and the ordered list of format-affecting mini-drivers.
    /// No JSON, no reflection, no LINQ.
    /// </summary>
    public readonly struct DeviceManifest
    {
        // Layout (little-endian):
        // [ 0..11]  Magic "SILICA.STACK"
        // [12..15]  u32 ManifestVersion = 1
        // [16..19]  u32 LogicalBlockSize
        // [20..23]  u32 EntryCount (max limited by frame size)
        // [24..39]  u128 DeviceUuid (two u64: Lo, Hi)
        // [40..??]  Entries[EntryCount] (each 32 bytes):
        //            u8 Kind; u8 Flags; u16 Reserved0;
        //            u32 Param1; u32 Param2; u32 Param3; u32 Param4;
        //            u64 Reserved1;
        // [end-4..end-1] u32 Checksum over all previous bytes
        public readonly struct Entry
        {
            public readonly byte Kind;   // see MiniDriverKind
            public readonly byte Flags;  // reserved for future
            public readonly ushort Reserved0;
            public readonly uint Param1;
            public readonly uint Param2;
            public readonly uint Param3;
            public readonly uint Param4;
            public readonly ulong Reserved1;

            public Entry(byte kind, byte flags, uint p1, uint p2, uint p3, uint p4)
            {
                Kind = kind;
                Flags = flags;
                Reserved0 = 0;
                Param1 = p1;
                Param2 = p2;
                Param3 = p3;
                Param4 = p4;
                Reserved1 = 0;
            }
        }

        public const uint ManifestVersion = 1;
        private static ReadOnlySpan<byte> Magic => new byte[] {
            (byte)'S',(byte)'I',(byte)'L',(byte)'I',(byte)'C',(byte)'A',
            (byte)'.',(byte)'S',(byte)'T',(byte)'A',(byte)'C',(byte)'K'
        };

        public readonly int LogicalBlockSize;
        public readonly ulong DeviceUuidLo;
        public readonly ulong DeviceUuidHi;
        public readonly Entry[] Entries;

        public DeviceManifest(int logicalBlockSize, ulong uuidLo, ulong uuidHi, Entry[] entries)
        {
            LogicalBlockSize = logicalBlockSize;
            DeviceUuidLo = uuidLo;
            DeviceUuidHi = uuidHi;
            Entries = entries ?? Array.Empty<Entry>();
        }

        public static int GetSerializedLength(int logicalBlockSize, int entryCount)
        {
            int header = 40;
            int entriesBytes = entryCount * 32;
            int total = header + entriesBytes + 4;
            // Round up to exactly one block for sanity, but writer uses provided span length
            if (total > logicalBlockSize) return total; // caller must ensure frame capacity
            return logicalBlockSize;
        }

        public bool TrySerialize(Span<byte> dest)
        {
            if (dest.Length < 64) return false;
            var span = dest;
            // Magic
            Magic.CopyTo(span.Slice(0, 12));
            // Version
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), ManifestVersion);
            // LBS
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), (uint)LogicalBlockSize);
            // EntryCount
            uint ec = (uint)(Entries == null ? 0 : Entries.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), ec);
            // UUID
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), DeviceUuidLo);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32, 8), DeviceUuidHi);
            // Entries
            int off = 40;
            for (int i = 0; i < ec; i++)
            {
                if (off + 32 > span.Length) return false;
                var e = Entries[i];
                span[off + 0] = e.Kind;
                span[off + 1] = e.Flags;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(off + 2, 2), e.Reserved0);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 4, 4), e.Param1);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 8, 4), e.Param2);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 12, 4), e.Param3);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 16, 4), e.Param4);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(off + 20, 8), e.Reserved1);
                off += 32;
            }
            // Zero-fill remainder except checksum
            int checksumPos = span.Length - 4;
            if (off < checksumPos)
            {
                span.Slice(off, checksumPos - off).Clear();
            }
            // Checksum
            uint sum = ComputeChecksum(span.Slice(0, checksumPos));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(checksumPos, 4), sum);
            return true;
        }

        public static bool TryDeserialize(ReadOnlySpan<byte> src, out DeviceManifest manifest)
        {
            manifest = default;
            if (src.Length < 64) return false;
            if (!src.Slice(0, 12).SequenceEqual(Magic)) return false;
            var ver = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4));
            if (ver != ManifestVersion) return false;
            var lbs = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(16, 4));
            var ec = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(20, 4));
            var uuidLo = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(24, 8));
            var uuidHi = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(32, 8));
            int off = 40;
            int checksumPos = src.Length - 4;
            if (ec < 0) return false;
            if (off + ec * 32 > checksumPos) return false;
            var entries = ec == 0 ? Array.Empty<Entry>() : new Entry[ec];
            for (int i = 0; i < ec; i++)
            {
                byte kind = src[off + 0];
                byte flags = src[off + 1];
                // u16 reserved0 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(off+2,2));
                uint p1 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 4, 4));
                uint p2 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 8, 4));
                uint p3 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 12, 4));
                uint p4 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 16, 4));
                // u64 reserved1 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(off+20,8));
                entries[i] = new Entry(kind, flags, p1, p2, p3, p4);
                off += 32;
            }
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(checksumPos, 4));
            uint actual = ComputeChecksum(src.Slice(0, checksumPos));
            if (expected != actual) return false;
            manifest = new DeviceManifest(lbs, uuidLo, uuidHi, entries);
            return true;
        }

        private static uint ComputeChecksum(ReadOnlySpan<byte> data)
        {
            // Simple 32-bit additive checksum (deterministic, fast).
            // Intentional: not cryptographic.
            uint sum = 0;
            int i = 0;
            int n = data.Length;
            while (i + 4 <= n)
            {
                sum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
                i += 4;
            }
            while (i < n) { sum += data[i]; i++; }
            return sum;
        }

        public bool FormatAffectsLayoutEquals(in DeviceManifest other)
        {
            // Compare only format-affecting parts: LBS and entry list content/order.
            if (LogicalBlockSize != other.LogicalBlockSize) return false;
            int ecA = Entries == null ? 0 : Entries.Length;
            int ecB = other.Entries == null ? 0 : other.Entries.Length;
            if (ecA != ecB) return false;
            for (int i = 0; i < ecA; i++)
            {
                var a = Entries[i];
                var b = other.Entries[i];
                if (a.Kind != b.Kind) return false;
                if (a.Flags != b.Flags) return false;
                if (a.Param1 != b.Param1) return false;
                if (a.Param2 != b.Param2) return false;
                if (a.Param3 != b.Param3) return false;
                if (a.Param4 != b.Param4) return false;
            }
            return true;
        }
    }

    public static class MiniDriverKind
    {
        // Stable, low-cardinality IDs for format-affecting drivers only.
        public const byte None = 0;
        public const byte Compression = 1;
        public const byte Encryption = 2;
        public const byte Versioning = 3;
        // Reserved: 4..31
    }
}
