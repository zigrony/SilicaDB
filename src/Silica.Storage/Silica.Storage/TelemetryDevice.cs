using System;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Interfaces;

namespace Silica.Storage
{
    // Filename: \Silica.Storage\Silica.Storage\TelemetryDevice.cs

    namespace Silica.Storage
    {
        public sealed class TelemetryDevice : IStorageDevice
        {
            private long _reads;
            private long _writes;

            public TelemetryDevice(
                int logicalBlockSize = 4096,
                int maxIoBytes = 4 * 1024 * 1024,
                bool requiresAlignedIo = false,
                bool supportsFua = false)
            {
                Geometry = new StorageGeometry
                {
                    LogicalBlockSize = logicalBlockSize,
                    MaxIoBytes = maxIoBytes,
                    RequiresAlignedIo = requiresAlignedIo,
                    SupportsFua = supportsFua
                };
            }

            public StorageGeometry Geometry { get; }

            public (long Reads, long Writes) Snapshot() => (_reads, _writes);

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            {
                Interlocked.Increment(ref _reads);

                int limit = Geometry.MaxIoBytes > 0 ? Math.Min(buffer.Length, Geometry.MaxIoBytes) : buffer.Length;
                var span = buffer.Span.Slice(0, limit);

                // Pattern: first 8 bytes = little-endian offset; rest zero.
                Span<byte> tmp = stackalloc byte[sizeof(long)];
                BitConverter.TryWriteBytes(tmp, offset);

                int toCopy = Math.Min(span.Length, tmp.Length);
                tmp.Slice(0, toCopy).CopyTo(span);
                if (span.Length > toCopy)
                    span.Slice(toCopy).Clear();

                return ValueTask.FromResult(span.Length);
            }

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default)
            {
                Interlocked.Increment(ref _writes);
                // Discard write; telemetry-only.
                return ValueTask.CompletedTask;
            }

            public ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
            {
                // Use same pattern as offset-based read, but seed with frameId.
                Interlocked.Increment(ref _reads);

                int limit = Geometry.MaxIoBytes > 0 ? Math.Min(buffer.Length, Geometry.MaxIoBytes) : buffer.Length;
                var span = buffer.Span.Slice(0, limit);

                Span<byte> tmp = stackalloc byte[sizeof(long)];
                BitConverter.TryWriteBytes(tmp, frameId);

                int toCopy = Math.Min(span.Length, tmp.Length);
                tmp.Slice(0, toCopy).CopyTo(span);
                if (span.Length > toCopy)
                    span.Slice(toCopy).Clear();

                return ValueTask.FromResult(span.Length);
            }

            public ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
            {
                Interlocked.Increment(ref _writes);
                // Discard write; telemetry-only.
                return ValueTask.CompletedTask;
            }

            public ValueTask FlushAsync(CancellationToken token = default) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}