// File: Silica.Durability/WalPayload.cs
using System;
using System.Buffers.Binary;

namespace Silica.Durability
{
    /// <summary>
    /// Safe, allocation-free helpers for reading primitive fields from a WAL payload.
    /// Keeps the WAL payload opaque; higher layers own the schema.
    /// </summary>
    public static class WalPayload
    {
        /// <summary>
        /// Returns true if the payload contains at least one byte and exposes it as 'kind'.
        /// Convention: Transactions layer can place a 1-byte kind code at payload[0].
        /// </summary>
        public static bool TryReadKind(ReadOnlySpan<byte> payload, out byte kind)
        {
            if (payload.Length < 1) { kind = 0; return false; }
            kind = payload[0];
            return true;
        }

        public static bool TryReadInt32(ReadOnlySpan<byte> payload, int offset, out int value)
        {
            if ((uint)(offset + 4) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            return true;
        }

        public static bool TryReadUInt32(ReadOnlySpan<byte> payload, int offset, out uint value)
        {
            if ((uint)(offset + 4) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            return true;
        }

        public static bool TryReadInt64(ReadOnlySpan<byte> payload, int offset, out long value)
        {
            if ((uint)(offset + 8) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8));
            return true;
        }

        public static bool TryReadUInt64(ReadOnlySpan<byte> payload, int offset, out ulong value)
        {
            if ((uint)(offset + 8) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
            return true;
        }

        public static bool TryReadInt16(ReadOnlySpan<byte> payload, int offset, out short value)
        {
            if ((uint)(offset + 2) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, 2));
            return true;
        }

        public static bool TryReadUInt16(ReadOnlySpan<byte> payload, int offset, out ushort value)
        {
            if ((uint)(offset + 2) > (uint)payload.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
            return true;
        }

        public static bool TryReadByte(ReadOnlySpan<byte> payload, int offset, out byte value)
        {
            if ((uint)(offset + 1) > (uint)payload.Length) { value = 0; return false; }
            value = payload[offset];
            return true;
        }

        /// <summary>
        /// Returns a slice [offset, offset+length) as ReadOnlyMemory without allocation if in-bounds.
        /// </summary>
        public static bool TrySlice(ReadOnlyMemory<byte> payload, int offset, int length, out ReadOnlyMemory<byte> slice)
        {
            if (offset < 0 || length < 0 || offset > payload.Length || (offset + length) > payload.Length)
            {
                slice = default;
                return false;
            }
            slice = payload.Slice(offset, length);
            return true;
        }
    }
}
