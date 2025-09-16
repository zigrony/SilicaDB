using System;
using System.IO;
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
    /// A file-system backed block device. Creates the file if it does not exist.
    /// Uses positional, handle-based I/O (RandomAccess.ReadAsync/WriteAsync)
    /// to allow concurrent reads/writes without a global seek lock.
    /// </summary>
    public class PhysicalBlockDevice : AsyncMiniDriver
    {
        private readonly string _path;
        private FileStream? _fs;

        // DiagnosticsCore handled by AsyncMiniDriver base

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
            if (path is null) throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidGeometryException("Path must be a non-empty string.");
            _path = path;
        }

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            // Ensure directory exists when a directory is part of the path.
            // This is a pragmatic hardening to avoid mount failures due to missing directories.
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch
            {
                // Preserve original behavior on file open; directory creation failures
                // will naturally manifest on FileStream construction below if relevant.
            }

            _fs = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                // Allow delete sharing for operational flexibility (rotate/rename-friendly).
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: Geometry.LogicalBlockSize,
                options: FileOptions.Asynchronous | FileOptions.RandomAccess);

            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            _fs?.Dispose();
            _fs = null;
            await Task.CompletedTask;
        }

        protected override async Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_fs is null)
                throw new DeviceNotMountedException();

            long offset;
            try
            {
                offset = checked(frameId * (long)Geometry.LogicalBlockSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOffsetException(frameId);
            }
            long endExclusive;
            try
            {
                endExclusive = checked(offset + Geometry.LogicalBlockSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOffsetException(frameId);
            }
            long lengthSnapshot = _fs.Length;

            if (endExclusive > lengthSnapshot)
                throw new DeviceReadOutOfRangeException(offset, Geometry.LogicalBlockSize, lengthSnapshot);

            int total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    int n = await RandomAccess.ReadAsync(
                        _fs.SafeFileHandle,
                        buffer.Slice(total),
                        offset + total,
                        cancellationToken).ConfigureAwait(false);

                    if (n == 0)
                        throw new DeviceReadOutOfRangeException(
                            offset: offset + total,
                            requestedLength: buffer.Length - total,
                            deviceLength: lengthSnapshot);

                    total += n;
                }
            }
            catch (ObjectDisposedException)
            {
                // Map late/tear-down races into canonical storage taxonomy.
                throw new DeviceNotMountedException();
            }

        }

        protected override async Task WriteFrameInternalAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            if (_fs is null)
                throw new DeviceNotMountedException();

            long offset;
            try
            {
                offset = checked(frameId * (long)Geometry.LogicalBlockSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOffsetException(frameId);
            }

            try
            {
                await RandomAccess.WriteAsync(
                        _fs.SafeFileHandle,
                        data,
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Map late/tear-down races into canonical storage taxonomy.
                throw new DeviceNotMountedException();
            }
            // Base will record operation metrics around this call.
        }

        protected override Task FlushAsyncInternal(CancellationToken cancellationToken)
        {
            if (_fs is null)
                throw new DeviceNotMountedException();
            return _fs.FlushAsync(cancellationToken);
        }
    }
}
