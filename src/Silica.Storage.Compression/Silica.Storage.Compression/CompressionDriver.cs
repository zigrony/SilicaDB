using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Compression
{
    /// <summary>
    /// Block device decorator that compresses each frame using Deflate.
    /// Frame layout: [u32_le compressed_length][compressed bytes][zero padding]
    /// </summary>
    public sealed class CompressionDriver : IStorageDevice, IMountableStorage
    {
        private readonly IStorageDevice _inner;
        private readonly CompressionOptions _options;
        private const int HeaderSize = 4; // u32 LE compressed length

        public CompressionDriver(IStorageDevice inner, CompressionOptions options)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public StorageGeometry Geometry => _inner.Geometry;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            // Offset/length I/O: forward unchanged (compression is per-frame only).
            return _inner.ReadAsync(offset, buffer, ct);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // Offset/length I/O: forward unchanged (compression is per-frame only).
            return _inner.WriteAsync(offset, buffer, ct);
        }

        public async ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken ct = default)
        {
            // Read the entire compressed frame into a temp buffer, then decompress payload into 'buffer'.
            int frameSize = Geometry.LogicalBlockSize;
            if (buffer.Length != frameSize)
                throw new ArgumentException("Frame read requires exact LogicalBlockSize.", nameof(buffer));

            byte[] frame = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                // Read compressed frame from inner
                var tmp = frame.AsMemory(0, frameSize);
                int n = await _inner.ReadFrameAsync(frameId, tmp, ct).ConfigureAwait(false);
                if (n != frameSize)
                    throw new InvalidOperationException("Inner device returned unexpected frame size.");

                // Read header
                if (n < HeaderSize)
                    throw new InvalidOperationException("Compressed frame header missing.");
                int compLen = ReadUInt32LittleEndian(frame, 0);
                if (compLen < 0 || compLen > (frameSize - HeaderSize))
                    throw new InvalidOperationException("Compressed length out of bounds.");

                // Empty payload: zero-fill destination
                if (compLen == 0)
                {
                    buffer.Span.Clear();
                    return buffer.Length;
                }

                // Decompress using Deflate
                using (var ms = new MemoryStream(frame, HeaderSize, compLen, writable: false))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress, leaveOpen: true))
                {
                    // Copy into destination; ensure we read exactly up to frameSize
                    int total = 0;
                    while (total < frameSize)
                    {
                        int read = ds.Read(buffer.Span.Slice(total));
                        if (read == 0) break; // end of stream
                        total += read;
                    }
                    // Zero-pad remainder
                    if (total < frameSize)
                        buffer.Span.Slice(total).Clear();
                }
                return frameSize;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame, clearArray: true);
            }
        }

        public async ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // Compress 'buffer' and write header + compressed payload + zero padding to inner.
            int frameSize = Geometry.LogicalBlockSize;
            if (buffer.Length != frameSize)
                throw new ArgumentException("Frame write requires exact LogicalBlockSize.", nameof(buffer));

            // Rent a temp buffer for compressed frame (header + payload + padding)
            byte[] frame = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                // Compress into a temporary MemoryStream
                using var ms = new MemoryStream();
                var level = MapLevel(_options.Level);
                using (var ds = new DeflateStream(ms, level, leaveOpen: true))
                {
                    // Write source bytes to compressor
                    // Loop avoids large single Write that may cause stack issues; keeps it simple.
                    int offset = 0;
                    const int Chunk = 64 * 1024;
                    while (offset < frameSize)
                    {
                        int take = frameSize - offset;
                        if (take > Chunk) take = Chunk;
                        ds.Write(buffer.Span.Slice(offset, take));
                        offset += take;
                    }
                }

                // Get compressed bytes and check fit
                int compLen = (int)ms.Length;
                if (HeaderSize + compLen > frameSize)
                {
                    // In rare cases, compressed output may exceed input; fall back to storing raw with header.
                    // Header still carries "compressed_length" (here == frameSize - HeaderSize).
                    compLen = frameSize - HeaderSize;
                    // Copy raw input into payload region
                    buffer.Span.Slice(0, compLen).CopyTo(frame.AsSpan(HeaderSize, compLen));
                }
                else
                {
                    // Copy compressed payload into frame after header
                    // MemoryStream.GetBuffer() exposes internal buffer; use ToArray() to be safe and simple.
                    var payload = ms.ToArray();
                    if (payload.Length != compLen)
                        throw new InvalidOperationException("Compressed payload length mismatch.");
                    payload.AsSpan().CopyTo(frame.AsSpan(HeaderSize, compLen));
                }

                // Write header
                WriteUInt32LittleEndian(frame, 0, compLen);
                // Zero-pad remainder
                if (HeaderSize + compLen < frameSize)
                    frame.AsSpan(HeaderSize + compLen, frameSize - (HeaderSize + compLen)).Clear();

                // Write full frame to inner
                await _inner.WriteFrameAsync(frameId, frame.AsMemory(0, frameSize), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame, clearArray: true);
            }
        }

        public ValueTask FlushAsync(CancellationToken ct = default)
            => _inner.FlushAsync(ct);

        public ValueTask DisposeAsync()
            => _inner.DisposeAsync();

        // --- IMountableStorage ---
        public Task MountAsync(CancellationToken cancellationToken = default)
        {
            if (_inner is IMountableStorage m)
                return m.MountAsync(cancellationToken);
            return Task.CompletedTask;
        }

        public Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            if (_inner is IMountableStorage m)
                return m.UnmountAsync(cancellationToken);
            return Task.CompletedTask;
        }

        // --------- Helpers (no LINQ, no reflection) ----------
        private static CompressionLevel MapLevel(int level)
        {
            // Map simple int to CompressionLevel without LINQ/reflection
            // 0 => NoCompression (store), 1 => Fastest, 2+ => Optimal
            if (level <= 0) return CompressionLevel.NoCompression;
            if (level == 1) return CompressionLevel.Fastest;
            return CompressionLevel.Optimal;
        }

        private static int ReadUInt32LittleEndian(byte[] src, int offset)
        {
            // Avoid BinaryPrimitives to keep dependencies minimal
            // LE: b0 + b1<<8 + b2<<16 + b3<<24
            int b0 = src[offset + 0];
            int b1 = src[offset + 1] << 8;
            int b2 = src[offset + 2] << 16;
            int b3 = src[offset + 3] << 24;
            return b0 | b1 | b2 | b3;
        }

        private static void WriteUInt32LittleEndian(byte[] dst, int offset, int value)
        {
            dst[offset + 0] = (byte)(value & 0xFF);
            dst[offset + 1] = (byte)((value >> 8) & 0xFF);
            dst[offset + 2] = (byte)((value >> 16) & 0xFF);
            dst[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
