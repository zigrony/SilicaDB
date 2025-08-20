using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;
using Silica.Observability.Tracing;

namespace Silica.Storage.Devices
{
    /// <summary>
    /// A file-system backed block device. Creates the file if it does not exist.
    /// Uses positional, handle-based I/O (RandomAccess.ReadAsync/WriteAsync)
    /// to allow concurrent reads/writes without a global seek lock.
    /// </summary>
    public class PhysicalBlockDevice : AsyncMiniDriver
    {
        private readonly string _path;
        private FileStream? _fs;

        // You can parameterize this if needed
        public override StorageGeometry Geometry { get; } = new StorageGeometry
        {
            LogicalBlockSize = 4096,
            MaxIoBytes = 1 << 20,
            RequiresAlignedIo = false,
            SupportsFua = false
        };

        public PhysicalBlockDevice(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device, "OnMountAsync", GetType().Name);

            _fs = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: Geometry.LogicalBlockSize,
                options: FileOptions.Asynchronous);

            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device, "OnUnmountAsync", GetType().Name);

            _fs?.Dispose();
            _fs = null;
            await Task.CompletedTask;
        }

        protected override async Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device, "ReadFrameInternalAsync", $"{GetType().Name}:{frameId}");

            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            long offset = checked(frameId * (long)Geometry.LogicalBlockSize);
            long endExclusive = checked(offset + Geometry.LogicalBlockSize);
            long lengthSnapshot = _fs.Length;

            if (endExclusive > lengthSnapshot)
                throw new DeviceReadOutOfRangeException(offset, Geometry.LogicalBlockSize, lengthSnapshot);

            int total = 0;
            while (total < buffer.Length)
            {
                int n = await RandomAccess.ReadAsync(
                    _fs.SafeFileHandle,
                    buffer.Slice(total),
                    offset + total,
                    cancellationToken).ConfigureAwait(false);

                if (n == 0)
                    throw new IOException($"Short read at offset {offset + total}, expected {buffer.Length - total} more bytes.");

                total += n;
            }
        }

        protected override async Task WriteFrameInternalAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device, "WriteFrameInternalAsync", $"{GetType().Name}:{frameId}");

            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            long offset = checked(frameId * (long)Geometry.LogicalBlockSize);

            await RandomAccess.WriteAsync(
                    _fs.SafeFileHandle,
                    data,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public override ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            // Wrap Task in ValueTask per AsyncMiniDriver contract
            return _fs.FlushAsync(cancellationToken).AsValueTask();
        }
    }
}
