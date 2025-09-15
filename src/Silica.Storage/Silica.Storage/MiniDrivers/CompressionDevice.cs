using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Decorators;
using Silica.Storage.Interfaces;

namespace Silica.Storage.MiniDrivers
{
    /// <summary>
    /// Frame-preserving compression decorator.
    /// First pass: no-op transform to validate loader + manifest plumbing.
    /// Future: compress within the fixed frame with internal header/padding.
    /// </summary>
    public sealed class CompressionDevice : StorageDeviceDecorator
    {
        private readonly uint _algorithm; // e.g., 1 = LZ4 (placeholder)
        private readonly uint _level;     // compression level

        public CompressionDevice(IStorageDevice inner, uint algorithm, uint level)
            : base(inner)
        {
            _algorithm = algorithm;
            _level = level;
        }

        // For now pass-through; future: implement frame-level compression header inside the 8192 bytes.
        public override ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> buffer, CancellationToken token = default)
            => base.ReadFrameAsync(frameId, buffer, token);

        public override ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> data, CancellationToken token = default)
            => base.WriteFrameAsync(frameId, data, token);
    }
}
