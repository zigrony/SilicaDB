// Common/ChecksumHelper.cs
using System;

namespace Silica.Common
{
    /// <summary>
    /// CRC32-based checksum computations for data integrity.
    /// </summary>
    internal static class ChecksumHelper
    {
        // Precomputed CRC32 table
        private static readonly uint[] Table;

        static ChecksumHelper()
        {
            const uint poly = 0xEDB88320u;
            Table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ poly;
                    else
                        crc >>= 1;
                }
                Table[i] = crc;
            }
        }

        /// <summary>
        /// Computes the CRC32 of an entire byte array.
        /// </summary>
        public static uint ComputeCRC32(byte[] data)
        {
            Guard.NotNull(data, nameof(data));
            return ComputeCRC32(data, 0, data.Length);
        }

        /// <summary>
        /// Computes the CRC32 of a segment of a byte array.
        /// </summary>
        public static uint ComputeCRC32(byte[] data, int offset, int length)
        {
            Guard.NotNull(data, nameof(data));
            Guard.InRange(offset, 0, data.Length, nameof(offset));
            Guard.InRange(length, 0, data.Length - offset, nameof(length));

            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + length; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            }
            return ~crc;
        }
    }
}
