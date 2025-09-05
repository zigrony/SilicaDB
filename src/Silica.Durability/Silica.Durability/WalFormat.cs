// Filename: Silica.Durability\WalFormat.cs

using System;
using System.Buffers.Binary;
using Silica.Common;

namespace Silica.Durability
{
    internal static class WalFormat
    {
        // "SLWA" (Silica WAL)
        internal const uint Magic = 0x534C5741;
        internal const uint Version = 1;
        // Defensive cap to prevent pathological allocations on corrupt tails.
        internal const int MaxRecordBytes = 64 * 1024 * 1024; // 64 MB

        // New header layout (24 bytes):
        // [4 magic][4 version][8 lsn][4 len][4 crc32c(payload)]
        internal const int NewHeaderSize = 24;
        internal const int LegacyHeaderSize = 12; // [8 lsn][4 len]

        internal static void WriteHeader(
            Span<byte> header,
            long lsn,
            int payloadLength,
            uint payloadCrc32C)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), Version);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(8, 8), lsn);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), payloadLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), payloadCrc32C);
        }

        internal static uint ComputeCrc32C(ReadOnlySpan<byte> payload)
        {
            return ChecksumHelper.ComputeCRC32C(payload);
        }

        internal static bool TryParseNewHeader(
            ReadOnlySpan<byte> header,
            out long lsn,
            out int length,
            out uint crc32c)
        {
            lsn = 0;
            length = 0;
            crc32c = 0;

            if (header.Length < NewHeaderSize)
                return false;

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
            if (magic != Magic)
                return false;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            if (version != Version)
                return false;

            lsn = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(8, 8));
            length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
            crc32c = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
            return true;
        }
    }
}
