using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Devices
{
    /// <summary>
    /// Backed by a growing MemoryStream.
    /// </summary>
    public class InMemoryDevice : AsyncStorageDeviceBase
    {
        private MemoryStream _ms;

        protected override Task OnMountAsync(CancellationToken cancellationToken)
        {
            _ms = new MemoryStream();
            return Task.CompletedTask;
        }

        protected override Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            _ms.Dispose();
            _ms = null!;
            return Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken)
        {
            var buffer = new byte[FrameSize];
            long offset = checked(frameId * (long)FrameSize);

            // If beyond current length, return zeros
            if (offset >= _ms.Length)
                return buffer;

            _ms.Seek(offset, SeekOrigin.Begin);
            int read = await _ms.ReadAsync(buffer, 0, FrameSize, cancellationToken)
                                 .ConfigureAwait(false);
            // leftover bytes are already zero
            return buffer;
        }

        protected override async Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken)
        {
            long offset = checked(frameId * (long)FrameSize);

            _ms.Seek(offset, SeekOrigin.Begin);
            await _ms.WriteAsync(data, 0, FrameSize, cancellationToken)
                     .ConfigureAwait(false);
        }
    }
}
