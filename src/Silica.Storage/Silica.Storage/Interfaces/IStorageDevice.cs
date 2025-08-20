// Filename: \Silica.Storage\Interfaces\IStorageDevice.cs

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Storage.Interfaces
{
    public readonly struct StorageGeometry
    {
        public int LogicalBlockSize { get; init; }
        public int MaxIoBytes { get; init; }
        public bool RequiresAlignedIo { get; init; }
        public bool SupportsFua { get; init; }
    }

    public interface IStorageDevice : IAsyncDisposable
    {
        StorageGeometry Geometry { get; }

        // Offset/length I/O
        ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default);
        ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken token = default);

        // Frame-granular I/O
        ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default);
        ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default);

        // Optional durability flush
        ValueTask FlushAsync(CancellationToken token = default);
    }
}
