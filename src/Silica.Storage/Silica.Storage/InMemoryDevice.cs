using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.DiagnosticsCore.Extensions.Storage; // StorageMetrics

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
                // Explicit span-to-span copy to avoid ambiguous overloads and guarantee correctness
                existing.AsSpan().CopyTo(buffer.Span);
                // Cache hit for in-memory page
                try { StorageMetrics.IncrementCacheHit(Metrics); } catch { }
                return Task.CompletedTask;
            }

            // Cache miss (absent page) — surface metric before throwing
            try { StorageMetrics.IncrementCacheMiss(Metrics); } catch { }

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

        protected override Task FlushAsyncInternal(CancellationToken cancellationToken)
            => Task.CompletedTask; // Nothing to flush for in-memory device

    }
}
