using System;

namespace Silica.Storage.Encryption
{
    internal sealed class SaltXorFrameIdV1 : ICounterDerivation
    {
        public static readonly SaltXorFrameIdV1 Instance = new SaltXorFrameIdV1();

        private SaltXorFrameIdV1() { }

        public void Derive(ReadOnlySpan<byte> salt, long frameId, Span<byte> ctr)
        {
            // Top 8 bytes: salt[0..7]
            salt.Slice(0, 8).CopyTo(ctr.Slice(0, 8));

            // Bottom 8 bytes: salt[8..15] XOR frameId (big-endian)
            ulong id = unchecked((ulong)frameId);
            ctr[8] = (byte)(salt[8] ^ ((id >> 56) & 0xFF));
            ctr[9] = (byte)(salt[9] ^ ((id >> 48) & 0xFF));
            ctr[10] = (byte)(salt[10] ^ ((id >> 40) & 0xFF));
            ctr[11] = (byte)(salt[11] ^ ((id >> 32) & 0xFF));
            ctr[12] = (byte)(salt[12] ^ ((id >> 24) & 0xFF));
            ctr[13] = (byte)(salt[13] ^ ((id >> 16) & 0xFF));
            ctr[14] = (byte)(salt[14] ^ ((id >> 8) & 0xFF));
            ctr[15] = (byte)(salt[15] ^ (id & 0xFF));
        }
    }
}
