using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Silica.Observability.Tracing;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Devices
{
    /// <summary>
    /// In-memory block device that supports concurrent,
    /// page-aligned I/O. Stores frames in a ConcurrentDictionary.
    /// </summary>
    public class InMemoryDevice : AsyncMiniDriver
    {
        // One entry per frameId
        private readonly ConcurrentDictionary<long, byte[]> _pages = new();

        // Define default geometry
        public override StorageGeometry Geometry { get; } = new StorageGeometry
        {
            LogicalBlockSize = 4096,
            MaxIoBytes = 1 << 20,
            RequiresAlignedIo = false,
            SupportsFua = false
        };

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device, "OnMountAsync", GetType().Name);

            _pages.Clear();
            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device, "OnUnmountAsync", GetType().Name);

            _pages.Clear();
            await Task.CompletedTask;
        }

        protected override Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_pages.TryGetValue(frameId, out var existing))
            {
                existing.CopyTo(buffer);
                return Task.CompletedTask;
            }

            throw new DeviceReadOutOfRangeException(
                offset: frameId * Geometry.LogicalBlockSize,
                requestedLength: Geometry.LogicalBlockSize,
                deviceLength: frameId * Geometry.LogicalBlockSize);
        }

        protected override Task WriteFrameInternalAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            // copy to ensure caller's buffer can be reused
            var frame = new byte[Geometry.LogicalBlockSize];
            data.CopyTo(frame);
            _pages[frameId] = frame;
            return Task.CompletedTask;
        }

        public override ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            // Nothing to flush for in-memory device
            return ValueTask.CompletedTask;
        }
    }
}
