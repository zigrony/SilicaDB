// Common/BinaryEncodingHelper.cs
using System;
using System.IO;
using System.Text;

namespace SilicaDB.Common
{
    /// <summary>
    /// Helpers for reading and writing fixed- and length-prefixed strings in binary form.
    /// </summary>
    internal static class BinaryEncodingHelper
    {
        /// <summary>
        /// Writes a string as a fixed-length Unicode byte array, padded or truncated to fit exactly.
        /// </summary>
        public static void WriteFixedString(BinaryWriter writer, string value, int fixedByteLength)
        {
            Guard.NotNull(writer, nameof(writer));
            Guard.InRange(fixedByteLength, 0, int.MaxValue, nameof(fixedByteLength));

            var bytes = Encoding.Unicode.GetBytes(value ?? string.Empty);
            if (bytes.Length > fixedByteLength)
                throw new ArgumentOutOfRangeException($"String is {bytes.Length} bytes, exceeds fixed length {fixedByteLength}.");

            writer.Write(bytes);
            if (bytes.Length < fixedByteLength)
                writer.WritePadding(fixedByteLength - bytes.Length);

        }

        /// <summary>
        /// Reads exactly <paramref name="fixedByteLength"/> bytes and returns the Unicode string up to the last non-zero byte.
        /// </summary>
        public static string ReadFixedString(BinaryReader reader, int fixedByteLength)
        {
            Guard.NotNull(reader, nameof(reader));
            Guard.InRange(fixedByteLength, 0, int.MaxValue, nameof(fixedByteLength));

            var buffer = reader.ReadBytes(fixedByteLength);
            int lastNonZero = Array.FindLastIndex(buffer, b => b != 0);
            if (lastNonZero < 0)
                return string.Empty;

            return Encoding.Unicode.GetString(buffer, 0, lastNonZero + 1);
        }

        /// <summary>
        /// Writes a UTF-8 length-prefixed string (4-byte length prefix, then data).
        /// </summary>
        public static void WriteLengthPrefixedString(BinaryWriter writer, string value)
        {
            Guard.NotNull(writer, nameof(writer));
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            Guard.InRange(bytes.Length, 0, int.MaxValue, nameof(value));

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>
        /// Reads a 4-byte length prefix, then that many UTF-8 bytes, returning the decoded string.
        /// </summary>
        public static string ReadLengthPrefixedString(BinaryReader reader)
        {
            Guard.NotNull(reader, nameof(reader));
            int length = reader.ReadInt32();
            Guard.InRange(length, 0, int.MaxValue, nameof(length));

            var bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
