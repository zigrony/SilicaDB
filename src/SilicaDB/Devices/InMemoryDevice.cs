using SilicaDB.Devices.Exceptions;
using System.Collections.Concurrent;
using SilicaDB.Diagnostics.Tracing;


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

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            // Trace the InMemory mount
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"OnMountAsync",GetType().Name);
            
            // nothing to initialize beyond clearing the dictionary
            _pages.Clear();
            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {

            // Trace the InMemory unmount
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"OnUnmountAsync",GetType().Name);

            // drop all pages
            _pages.Clear();
            await Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(
            long frameId,
            CancellationToken cancellationToken)
        {
            // Trace individual in-memory reads
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"ReadFrameInternalAsync",$"{GetType().Name}:{frameId}");

            if (_pages.TryGetValue(frameId, out var existing))
            {
                var copy = new byte[FrameSize];
                Buffer.BlockCopy(existing, 0, copy, 0, FrameSize);
                return copy;
            }

            // No concept of contiguous length here; signal out-of-range by frame id
            throw new DeviceReadOutOfRangeException(
                offset: frameId * FrameSize,
                requestedLength: FrameSize,
                deviceLength: frameId * FrameSize);  // self-signal out-of-range
        }

        protected override async Task WriteFrameInternalAsync(
            long frameId,
            byte[] data,
            CancellationToken cancellationToken)
        {
            // Trace individual in-memory writes
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"WriteFrameInternalAsync",$"{GetType().Name}:{frameId}");

            // data is exactly FrameSize bytes per base class contract
            // store a copy so the caller can reuse their buffer immediately
            var frame = new byte[FrameSize];
            Buffer.BlockCopy(data, 0, frame, 0, FrameSize);
            _pages[frameId] = frame;
            await Task.CompletedTask;
        }
        public override async Task FlushAsync(CancellationToken _)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"FlushAsync",GetType().Name);
            await Task.CompletedTask;
        }
    }
}
