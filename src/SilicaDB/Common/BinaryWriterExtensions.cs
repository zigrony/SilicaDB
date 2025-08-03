using System.Buffers;
using System.IO;

namespace SilicaDB.Common
{
    /// <summary>
    /// Extension methods for BinaryWriter.
    /// </summary>
    internal static class BinaryWriterExtensions
    {
        /// <summary>
        /// Writes <paramref name="count"/> zero bytes using ArrayPool<byte> to avoid per-call allocation.
        /// </summary>
        public static void WritePadding(this BinaryWriter writer, int count)
        {
            Guard.NotNull(writer, nameof(writer));
            Guard.InRange(count, 0, int.MaxValue, nameof(count));

            if (count == 0)
                return;

            var buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                Array.Clear(buffer, 0, count); // guarantee zeroes
                writer.Write(buffer, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
        }
    }
}
