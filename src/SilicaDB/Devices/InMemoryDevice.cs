using SilicaDB.Devices.Exceptions;
using System.Collections.Concurrent;

namespace SilicaDB.Devices
{
    /// <summary>
    /// In-memory block device that supports concurrent,
    /// page-aligned I/O like PhysicalBlockDevice but stores
    /// frames in a ConcurrentDictionary rather than a Stream.
    /// </summary>
    public class InMemoryDevice : AsyncStorageDeviceBase
    {
        // One entry per frameId
        private readonly ConcurrentDictionary<long, byte[]> _pages = new();

        protected override Task OnMountAsync(CancellationToken cancellationToken)
        {
            // nothing to initialize beyond clearing the dictionary
            _pages.Clear();
            return Task.CompletedTask;
        }

        protected override Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            // drop all pages
            _pages.Clear();
            return Task.CompletedTask;
        }

        protected override Task<byte[]> ReadFrameInternalAsync(
            long frameId,
            CancellationToken cancellationToken)
        {
            if (_pages.TryGetValue(frameId, out var existing))
            {
                var copy = new byte[FrameSize];
                Buffer.BlockCopy(existing, 0, copy, 0, FrameSize);
                return Task.FromResult(copy);
            }

            // No concept of contiguous length here; signal out-of-range by frame id
            throw new DeviceReadOutOfRangeException(
                offset: frameId * FrameSize,
                requestedLength: FrameSize,
                deviceLength: frameId * FrameSize);  // self-signal out-of-range
        }

        protected override Task WriteFrameInternalAsync(
            long frameId,
            byte[] data,
            CancellationToken cancellationToken)
        {
            // data is exactly FrameSize bytes per base class contract
            // store a copy so the caller can reuse their buffer immediately
            var frame = new byte[FrameSize];
            Buffer.BlockCopy(data, 0, frame, 0, FrameSize);
            _pages[frameId] = frame;
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken _) => Task.CompletedTask;

    }
}
