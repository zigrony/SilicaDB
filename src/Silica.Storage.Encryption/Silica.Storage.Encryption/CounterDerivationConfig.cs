using System;
using System.Buffers.Binary;

namespace Silica.Storage.Encryption
{
    public enum CounterDerivationKind : uint
    {
        // Deterministic baseline: salt XOR frameId (big-endian) in low 8 bytes.
        SaltXorFrameId_V1 = 1,

        // Deterministic with device-wide epoch mixed into low 8 bytes.
        SaltXorFrameId_Epoch_V1 = 2,

        // Reserved for future PRF-based seeds (e.g., AES-PRF).
        AesPrf_V1 = 10
    }

    public readonly struct CounterDerivationConfig
    {
        public readonly CounterDerivationKind Kind;
        public readonly uint Version;
        public readonly uint Flags;
        public readonly uint Epoch;

        public CounterDerivationConfig(CounterDerivationKind kind, uint version, uint flags, uint epoch)
        {
            Kind = kind;
            Version = version;
            Flags = flags;
            Epoch = epoch;
        }

        public static readonly string BlobKey = "enc.counter";

        // Fixed-size, 16-byte serialization (no allocations beyond the returned array).
        public static byte[] Serialize(CounterDerivationConfig c)
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)c.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), c.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), c.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), c.Epoch);
            return buf;
        }

        public static bool TryDeserialize(ReadOnlySpan<byte> src, out CounterDerivationConfig c)
        {
            c = default;
            if (src.Length < 16) return false;
            var kind = (CounterDerivationKind)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
            var ver = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
            var flg = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4));
            var ep = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4));
            c = new CounterDerivationConfig(kind, ver, flg, ep);
            return true;
        }
    }
}
