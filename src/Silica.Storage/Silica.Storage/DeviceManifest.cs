using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Silica.Storage
{
    /// <summary>
    /// Deterministic, binary manifest written to frame 0.
    /// Encodes geometry and the ordered list of format-affecting mini-drivers.
    /// No JSON, no reflection, no LINQ.
    ///
    /// V1 Layout (little-endian):
    /// [ 0..11]  Magic "SILICA.STACK"
    /// [12..15]  u32 ManifestVersion = 1
    /// [16..19]  u32 LogicalBlockSize
    /// [20..23]  u32 EntryCount (max limited by frame size)
    /// [24..39]  u128 DeviceUuid (two u64: Lo, Hi)
    /// [40..??]  Entries[EntryCount] (each 32 bytes):
    ///            u8 Kind; u8 Flags; u16 Reserved0;
    ///            u32 Param1; u32 Param2; u32 Param3; u32 Param4;
    ///            u64 Reserved1;
    /// [end-4..end-1] u32 Checksum over all previous bytes
    ///
    /// V2 Layout (adds per-entry metadata blobs; reader is backward compatible):
    /// [ 0..11]  Magic "SILICA.STACK"
    /// [12..15]  u32 ManifestVersion = 2
    /// [16..19]  u32 LogicalBlockSize
    /// [20..23]  u32 EntryCount
    /// [24..39]  u128 DeviceUuid (two u64: Lo, Hi)
    /// [40..??]  Entries[EntryCount] (each 32 bytes) — same as V1
    /// [..]      u32 BlobDirLengthBytes (0 => no blobs)
    /// [..]      BlobDir payload (u32 counts[EntryCount], then for each entry:
    ///            [u16 keyLen, key UTF8, u32 valLen, valBytes] repeated)
    /// [end-4..] u32 Checksum over all previous bytes
    /// </summary>
    public readonly struct DeviceManifest
    {
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
            // V2: optional, driver-owned named blobs; null or empty when not present.
            public readonly Dictionary<string, byte[]>? MetadataBlobs;

            public Entry(byte kind, byte flags, uint p1, uint p2, uint p3, uint p4,
                         Dictionary<string, byte[]>? blobs = null)
            {
                Kind = kind;
                Flags = flags;
                Reserved0 = 0;
                Param1 = p1;
                Param2 = p2;
                Param3 = p3;
                Param4 = p4;
                Reserved1 = 0;
                MetadataBlobs = blobs;
            }
        }

        public const uint ManifestVersionV1 = 1;
        public const uint ManifestVersionV2 = 2;
        public const uint ManifestVersionV3 = 3; // CRC32C integrity over payload (preferred writer)

        private static readonly byte[] s_magic = new byte[] {
            (byte)'S',(byte)'I',(byte)'L',(byte)'I',(byte)'C',(byte)'A',
            (byte)'.',(byte)'S',(byte)'T',(byte)'A',(byte)'C',(byte)'K'
        };
        private static ReadOnlySpan<byte> Magic => s_magic.AsSpan();

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
            if (total > logicalBlockSize) return total; // caller must ensure frame capacity
            return logicalBlockSize;
        }

        public bool TrySerialize(Span<byte> dest)
        {
            if (dest.Length < 64) return false;
            var span = dest;

            // Magic (fixed, non-allocating)
            Magic.CopyTo(span.Slice(0, 12));

            // Writer always emits V3 (CRC32C). Reader remains backward-compatible with V1/V2.
            int ec = Entries == null ? 0 : Entries.Length;
            uint version = ManifestVersionV3;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), version);

            // LBS
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), (uint)LogicalBlockSize);

            // EntryCount
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), (uint)ec);

            // UUID
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), DeviceUuidLo);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32, 8), DeviceUuidHi);

            // Entries table (fixed-size)
            int off = 40;
            int checksumPos = span.Length - 4;

            for (int i = 0; i < ec; i++)
            {
                if (off + 32 > checksumPos) return false;
                var e = Entries![i];
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

            // V2/V3 blob directory (optional). V3 retains the same layout and placement.
            bool anyBlobs = false;
            for (int i = 0; i < ec; i++)
            {
                if (Entries![i].MetadataBlobs is { Count: > 0 }) { anyBlobs = true; break; }
            }
            if (anyBlobs)
            {
                if (!TryWriteBlobDirectory(span, checksumPos, ec, Entries!, ref off))
                    return false;
            }

            // Zero-fill remainder except checksum
            if (off < checksumPos)
            {
                span.Slice(off, checksumPos - off).Clear();
            }

            // Checksum (algorithm depends on manifest version). Writer uses V3 CRC32C.
            uint sum = ComputeCrc32C(span.Slice(0, checksumPos));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(checksumPos, 4), sum);
            return true;
        }

        public static bool TryDeserialize(ReadOnlySpan<byte> src, out DeviceManifest manifest)
        {
            manifest = default;
            if (src.Length < 64) return false;
            if (!src.Slice(0, 12).SequenceEqual(Magic)) return false;

            var ver = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4));
            if (ver != ManifestVersionV1 && ver != ManifestVersionV2 && ver != ManifestVersionV3) return false;

            var lbs = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(16, 4));
            // Reject malformed or pathological LBS immediately to avoid trusting the remaining payload.
            if (lbs <= 0) return false;
            // Require power of two for predictable alignment and to match device geometry constraints.
            if ((lbs & (lbs - 1)) != 0) return false;
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
                uint p1 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 4, 4));
                uint p2 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 8, 4));
                uint p3 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 12, 4));
                uint p4 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(off + 16, 4));
                entries[i] = new Entry(kind, flags, p1, p2, p3, p4, blobs: null);
                off += 32;
            }

            if ((ver == ManifestVersionV2 || ver == ManifestVersionV3) && off < checksumPos)
            {
                // Best-effort blob directory parsing; failure => treat as no blobs
                TryReadBlobDirectory(src.Slice(off, checksumPos - off), entries);
            }

            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(checksumPos, 4));
            uint actual;
            if (ver == ManifestVersionV1 || ver == ManifestVersionV2)
            {
                // Legacy additive checksum
                actual = ComputeChecksum(src.Slice(0, checksumPos));
            }
            else
            {
                // V3 uses CRC32C (Castagnoli)
                actual = ComputeCrc32C(src.Slice(0, checksumPos));
            }
            if (expected != actual) return false;

            manifest = new DeviceManifest(lbs, uuidLo, uuidHi, entries);
            return true;
        }

        private static uint ComputeChecksum(ReadOnlySpan<byte> data)
        {
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

        // ---------------- CRC32C (Castagnoli) ----------------
        // Pure .NET implementation, no third-party dependencies. Table precomputed on first use.
        private static readonly uint[] s_crc32cTable = CreateCrc32CTable();

        private static uint[] CreateCrc32CTable()
        {
            // Polynomial 0x1EDC6F41 (reflected 0x82F63B78)
            const uint poly = 0x82F63B78u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1) != 0) c = poly ^ (c >> 1);
                    else c >>= 1;
                }
                table[i] = c;
            }
            return table;
        }

        private static uint ComputeCrc32C(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                uint idx = (crc ^ data[i]) & 0xFFu;
                crc = s_crc32cTable[idx] ^ (crc >> 8);
            }
            return ~crc;
        }

        public bool FormatAffectsLayoutEquals(in DeviceManifest other)
        {
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

                var ab = a.MetadataBlobs;
                var bb = b.MetadataBlobs;
                int ac = ab is null ? 0 : ab.Count;
                int bc = bb is null ? 0 : bb.Count;
                if (ac != bc) return false;
                if (ac > 0)
                {
                    foreach (var kv in ab!)
                    {
                        if (!bb!.TryGetValue(kv.Key, out var vb)) return false;
                        var va = kv.Value;
                        if (va is null && vb is null) continue;
                        if (va is null || vb is null) return false;
                        if (va.Length != vb.Length) return false;
                        if (!new ReadOnlySpan<byte>(va).SequenceEqual(vb)) return false;
                    }
                }
            }
            return true;
        }

        // ---------------- V2 Blob Directory helpers ----------------

        private static bool TryWriteBlobDirectory(Span<byte> span, int checksumPos, int ec, Entry[] entries, ref int off)
        {
            // Compute total payload length
            int payloadLen = 0;

            // Space for counts (u32 per entry)
            payloadLen += 4 * ec;

            // Compute per-entry items
            for (int i = 0; i < ec; i++)
            {
                var blobs = entries[i].MetadataBlobs;
                int count = blobs is null ? 0 : blobs.Count;

                if (count > 0)
                {
                    foreach (var kv in blobs!)
                    {
                        // u16 keyLen + key + u32 valLen + value
                        int keyLen = Encoding.UTF8.GetByteCount(kv.Key);
                        if (keyLen <= 0 || keyLen > ushort.MaxValue) return false;
                        int valLen = kv.Value?.Length ?? 0;
                        if (valLen < 0) return false;

                        payloadLen += 2;          // keyLen (u16)
                        payloadLen += keyLen;     // key bytes
                        payloadLen += 4;          // valLen (u32)
                        payloadLen += valLen;     // value bytes
                    }
                }
            }

            // We write: u32 BlobDirLengthBytes, then payload.
            int totalBytes = 4 + payloadLen;
            if (off + totalBytes > checksumPos) return false;

            // Write length
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off, 4), (uint)payloadLen);
            int pos = off + 4;

            // Write counts
            for (int i = 0; i < ec; i++)
            {
                var blobs = entries[i].MetadataBlobs;
                uint count = (uint)(blobs is null ? 0 : blobs.Count);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, 4), count);
                pos += 4;
            }

            // Write entries
            for (int i = 0; i < ec; i++)
            {
                var blobs = entries[i].MetadataBlobs;
                if (blobs is null || blobs.Count == 0) continue;

                // Deterministic order: sort by key
                // (Stable ordering improves reproducibility across runs)
                foreach (var kv in SortByKey(blobs))
                {
                    int keyLen = Encoding.UTF8.GetByteCount(kv.Key);
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(pos, 2), (ushort)keyLen);
                    pos += 2;

                    if (keyLen > 0)
                    {
                        pos += Encoding.UTF8.GetBytes(kv.Key, span.Slice(pos, keyLen));
                    }

                    var val = kv.Value ?? Array.Empty<byte>();
                    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, 4), (uint)val.Length);
                    pos += 4;

                    if (val.Length > 0)
                    {
                        new ReadOnlySpan<byte>(val).CopyTo(span.Slice(pos, val.Length));
                        pos += val.Length;
                    }
                }
            }

            off += totalBytes;
            return true;
        }

        private static void TryReadBlobDirectory(ReadOnlySpan<byte> src, Entry[] entries)
        {
            try
            {
                if (src.Length < 4) return;
                int dirLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
                if (dirLen <= 0 || 4 + dirLen > src.Length) return;

                var dir = src.Slice(4, dirLen);
                int ec = entries.Length;
                if (dir.Length < 4 * ec) return;

                int pos = 0;
                var counts = new int[ec];
                for (int i = 0; i < ec; i++)
                {
                    counts[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.Slice(pos, 4));
                    pos += 4;
                    if (counts[i] < 0) return;
                }

                for (int i = 0; i < ec; i++)
                {
                    Dictionary<string, byte[]>? dict = null;
                    int count = counts[i];
                    if (count > 0)
                    {
                        dict = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
                        for (int j = 0; j < count; j++)
                        {
                            if (pos + 2 > dir.Length) return;
                            int klen = BinaryPrimitives.ReadUInt16LittleEndian(dir.Slice(pos, 2));
                            pos += 2;
                            if (klen <= 0 || pos + klen > dir.Length) return;
                            string key = Encoding.UTF8.GetString(dir.Slice(pos, klen));
                            pos += klen;

                            if (pos + 4 > dir.Length) return;
                            int vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.Slice(pos, 4));
                            pos += 4;
                            if (vlen < 0 || pos + vlen > dir.Length) return;

                            byte[] val = vlen == 0 ? Array.Empty<byte>() : dir.Slice(pos, vlen).ToArray();
                            pos += vlen;

                            dict[key] = val;
                        }
                    }

                    if (dict is not null)
                    {
                        var e = entries[i];
                        entries[i] = new Entry(e.Kind, e.Flags, e.Param1, e.Param2, e.Param3, e.Param4, dict);
                    }
                }
            }
            catch
            {
                // Swallow: compatibility-first
            }
        }

        private static IEnumerable<KeyValuePair<string, byte[]>> SortByKey(Dictionary<string, byte[]> dict)
        {
            // Low overhead stable ordering by key
            var arr = new List<KeyValuePair<string, byte[]>>(dict.Count);
            foreach (var kv in dict) arr.Add(kv);
            arr.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            return arr;
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
