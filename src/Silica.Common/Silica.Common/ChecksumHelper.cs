// Common/ChecksumHelper.cs
using System;

namespace Silica.Common
{
    /// <summary>
    /// CRC32 and CRC32C checksum computations for data integrity.
    /// CRC32 uses polynomial 0xEDB88320 (reflected, ZIP/PNG standard).
    /// CRC32C uses polynomial 0x82F63B78 (Castagnoli, reflected).
    /// </summary>
    public static class ChecksumHelper
    {
        private static readonly uint[] TableCRC32 = InitTable(0xEDB88320u);
        private static readonly uint[] TableCRC32C = InitTable(0x82F63B78u);

        /// <summary>
        /// Initializes a 256-entry CRC lookup table for the given reflected polynomial.
        /// </summary>
        private static uint[] InitTable(uint poly)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        // ------------------------
        // CRC32 (0xEDB88320)
        // ------------------------

        /// <summary>
        /// Computes the CRC32 of an entire byte array.
        /// </summary>
        public static uint ComputeCRC32(byte[] data)
        {
            Guard.NotNull(data, nameof(data));
            return ComputeCRC32(data.AsSpan());
        }

        /// <summary>
        /// Computes the CRC32 of a segment of a byte array.
        /// </summary>
        public static uint ComputeCRC32(byte[] data, int offset, int length)
        {
            Guard.NotNull(data, nameof(data));
            Guard.InRange(offset, 0, data.Length, nameof(offset));
            Guard.InRange(length, 0, data.Length - offset, nameof(length));
            return ComputeCRC32(data.AsSpan(offset, length));
        }

        /// <summary>
        /// Computes the CRC32 of a read-only span of bytes.
        /// </summary>
        public static uint ComputeCRC32(ReadOnlySpan<byte> data) =>
            FinalizeCRC(0xFFFFFFFFu, data, TableCRC32);

        /// <summary>
        /// Continues a CRC32 computation from an existing CRC value.
        /// </summary>
        public static uint ComputeCRC32(uint seed, ReadOnlySpan<byte> data) =>
            FinalizeCRC(~seed, data, TableCRC32);

        // ------------------------
        // CRC32C (0x82F63B78)
        // ------------------------

        /// <summary>
        /// Computes the CRC32C (Castagnoli) of an entire byte array.
        /// </summary>
        public static uint ComputeCRC32C(byte[] data)
        {
            Guard.NotNull(data, nameof(data));
            return ComputeCRC32C(data.AsSpan());
        }

        /// <summary>
        /// Computes the CRC32C (Castagnoli) of a segment of a byte array.
        /// </summary>
        public static uint ComputeCRC32C(byte[] data, int offset, int length)
        {
            Guard.NotNull(data, nameof(data));
            Guard.InRange(offset, 0, data.Length, nameof(offset));
            Guard.InRange(length, 0, data.Length - offset, nameof(length));
            return ComputeCRC32C(data.AsSpan(offset, length));
        }

        /// <summary>
        /// Computes the CRC32C (Castagnoli) of a read-only span of bytes.
        /// </summary>
        public static uint ComputeCRC32C(ReadOnlySpan<byte> data) =>
            FinalizeCRC(0xFFFFFFFFu, data, TableCRC32C);

        /// <summary>
        /// Continues a CRC32C computation from an existing CRC value.
        /// </summary>
        public static uint ComputeCRC32C(uint seed, ReadOnlySpan<byte> data) =>
            FinalizeCRC(~seed, data, TableCRC32C);

        // ------------------------
        // Core loop
        // ------------------------

        private static uint FinalizeCRC(uint crc, ReadOnlySpan<byte> data, uint[] table)
        {
            foreach (var b in data)
                crc = (crc >> 8) ^ table[(crc ^ b) & 0xFF];
            return ~crc;
        }
    }
}
