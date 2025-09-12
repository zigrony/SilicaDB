using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.Storage.Metrics; // StorageMetrics

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
        private long _maxFrameWritten = -1; // -1 => empty device

        // Define default geometry
        public override StorageGeometry Geometry { get; } = new StorageGeometry
        {
            LogicalBlockSize = 4096,
            MaxIoBytes = 1 << 20,
            RequiresAlignedIo = false,
            SupportsFua = false
        };

        public InMemoryDevice() { /* Base registers metrics; no local registration needed. */ }

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            _pages.Clear();
            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {
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
                existing.AsSpan().CopyTo(buffer.Span);
                try { StorageMetrics.IncrementCacheHit(Metrics); } catch { }
                return Task.CompletedTask;
            }
            try { StorageMetrics.IncrementCacheMiss(Metrics); } catch { }
            var deviceLen = GetDeviceLength();
            throw new DeviceReadOutOfRangeException(
                offset: frameId * (long)Geometry.LogicalBlockSize,
                requestedLength: Geometry.LogicalBlockSize,
                deviceLength: deviceLen);
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
            UpdateMaxFrameWritten(frameId);
            return Task.CompletedTask;
        }

        protected override Task FlushAsyncInternal(CancellationToken cancellationToken)
            => Task.CompletedTask; // Nothing to flush for in-memory device

        // --- Length tracking helpers (enterprise-consistent EOF reporting) ---
        private void UpdateMaxFrameWritten(long frameId)
        {
            long candidate = frameId;
            long current;
            do
            {
                current = Interlocked.Read(ref _maxFrameWritten);
                if (candidate <= current) return;
            }
            while (Interlocked.CompareExchange(ref _maxFrameWritten, candidate, current) != current);
        }

        private long GetDeviceLength()
        {
            long max = Interlocked.Read(ref _maxFrameWritten);
            if (max < 0) return 0;
            long blocks = max + 1;
            long size = (long)Geometry.LogicalBlockSize;
            if (blocks > 0 && size > 0)
            {
                long limit = long.MaxValue / size;
                if (blocks > limit) return long.MaxValue;
            }
            return blocks * size;
        }
    }
}
