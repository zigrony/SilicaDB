// Filename: \Silica.Storage\Silica.Storage\LoopbackDevice.cs
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage
{
    public sealed class LoopbackDevice : IStorageDevice
    {
        private readonly ConcurrentDictionary<long, byte[]> _frames = new();
        private readonly int _frameSize;
        private readonly byte[] _zeroFrame;

        // Common defaults: no IO cap, no alignment requirement, no FUA
        public LoopbackDevice(
            int frameSize,
            int logicalBlockSize = 4096,
            int maxIoBytes = 0,
            bool requiresAlignedIo = false,
            bool supportsFua = false)
        {
            if (frameSize <= 0) throw new ArgumentOutOfRangeException(nameof(frameSize));
            _frameSize = frameSize;
            _zeroFrame = new byte[_frameSize];

            Geometry = new StorageGeometry
            {
                LogicalBlockSize = logicalBlockSize,
                MaxIoBytes = maxIoBytes,           // 0 => no artificial cap
                RequiresAlignedIo = requiresAlignedIo,
                SupportsFua = supportsFua
            };
        }

        public StorageGeometry Geometry { get; }

        public ValueTask FlushAsync(CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // -------- Frame-granular I/O --------

        public ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var span = buffer.Span;
            int toCopy = Math.Min(span.Length, _frameSize);
            var src = GetFrameSnapshot(frameId);
            src.AsSpan(0, toCopy).CopyTo(span);
            if (span.Length > toCopy)
                span.Slice(toCopy).Clear();

            return ValueTask.FromResult(toCopy);
        }

        public ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var src = data.Span;
            int toCopy = Math.Min(src.Length, _frameSize);

            var existing = GetFrameSnapshot(frameId);
            var next = new byte[_frameSize];
            // Start with existing snapshot
            existing.CopyTo(next, 0);
            // Overlay incoming data at offset 0
            src.Slice(0, toCopy).CopyTo(next.AsSpan(0, toCopy));

            _frames[frameId] = next;
            return ValueTask.CompletedTask;
        }

        // -------- Offset/length I/O --------

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (buffer.Length == 0) return ValueTask.FromResult(0);

            long curOffset = offset;
            int totalCopied = 0;
            var dst = buffer.Span;

            while (totalCopied < buffer.Length)
            {
                token.ThrowIfCancellationRequested();

                long frameId = Math.DivRem(curOffset, _frameSize, out long intra);
                int inFrameOffset = (int)intra;
                int canCopy = Math.Min(_frameSize - inFrameOffset, buffer.Length - totalCopied);

                var src = GetFrameSnapshot(frameId).AsSpan(inFrameOffset, canCopy);
                src.CopyTo(dst.Slice(totalCopied, canCopy));

                totalCopied += canCopy;
                curOffset += canCopy;
            }

            return ValueTask.FromResult(totalCopied);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (data.Length == 0) return ValueTask.CompletedTask;

            long curOffset = offset;
            int totalWritten = 0;
            var src = data.Span;

            while (totalWritten < data.Length)
            {
                token.ThrowIfCancellationRequested();

                long frameId = Math.DivRem(curOffset, _frameSize, out long intra);
                int inFrameOffset = (int)intra;
                int canCopy = Math.Min(_frameSize - inFrameOffset, data.Length - totalWritten);

                // Snapshot existing, create next, overlay the slice, then publish atomically
                var existing = GetFrameSnapshot(frameId);
                var next = new byte[_frameSize];
                existing.CopyTo(next, 0);
                src.Slice(totalWritten, canCopy).CopyTo(next.AsSpan(inFrameOffset, canCopy));
                _frames[frameId] = next;

                totalWritten += canCopy;
                curOffset += canCopy;
            }

            return ValueTask.CompletedTask;
        }

        // -------- Helpers --------

        private byte[] GetFrameSnapshot(long frameId)
        {
            return _frames.TryGetValue(frameId, out var arr) ? arr : _zeroFrame;
        }
    }
}
