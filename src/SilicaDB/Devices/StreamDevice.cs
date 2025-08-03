using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Devices
{
    /// <summary>
    /// Wraps any seekable stream for frame‐based I/O.
    /// </summary>
    public class StreamDevice : AsyncStorageDeviceBase
    {
        private readonly Stream _stream;
        private readonly bool _disposeStreamOnUnmount;

        public StreamDevice(Stream stream, bool disposeStreamOnUnmount = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
                throw new ArgumentException("Stream must support read/write/seek", nameof(stream));
            _disposeStreamOnUnmount = disposeStreamOnUnmount;
        }

        protected override Task OnMountAsync(CancellationToken cancellationToken)
        {
            // nothing to initialize
            return Task.CompletedTask;
        }

        protected override Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            if (_disposeStreamOnUnmount)
                _stream.Dispose();
            return Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken)
        {
            var buffer = new byte[FrameSize];
            long offset = checked(frameId * (long)FrameSize);

            if (offset >= _stream.Length)
                return buffer;

            _stream.Seek(offset, SeekOrigin.Begin);
            int read = await _stream.ReadAsync(buffer, 0, FrameSize, cancellationToken)
                                   .ConfigureAwait(false);
            return buffer;
        }

        protected override async Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken)
        {
            long offset = checked(frameId * (long)FrameSize);

            _stream.Seek(offset, SeekOrigin.Begin);
            await _stream.WriteAsync(data, 0, FrameSize, cancellationToken)
                         .ConfigureAwait(false);
        }
    }
}
