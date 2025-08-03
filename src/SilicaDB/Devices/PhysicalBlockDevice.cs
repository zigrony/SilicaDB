// File: PhysicalBlockDevice.cs
// Namespace: SilicaDB.Devices

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices.Interfaces;

namespace SilicaDB.Devices
{
    /// <summary>
    /// A file-system backed block device.  Creates the file if it does not exist.
    /// Uses a global stream lock to serialize seek+I/O while still allowing
    /// per-frame concurrency via the base class.
    /// </summary>
    public class PhysicalBlockDevice : AsyncStorageDeviceBase
    {
        private readonly string _path;
        private FileStream? _fs;
        private readonly SemaphoreSlim _streamLock = new SemaphoreSlim(1, 1);

        public PhysicalBlockDevice(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        protected override Task OnMountAsync(CancellationToken cancellationToken)
        {
            // Open or create for asynchronous R/W
            _fs = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: FrameSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            return Task.CompletedTask;
        }

        protected override Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            // Dispose underlying FileStream and the global stream lock
            _fs?.Dispose();
            _fs = null;
            _streamLock.Dispose();

            return Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken)
        {
            var buffer = new byte[FrameSize];
            long offset = checked(frameId * (long)FrameSize);

            // If reading beyond EOF, return zero-filled buffer
            if (offset >= _fs!.Length)
                return buffer;

            await _streamLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _fs.Seek(offset, SeekOrigin.Begin);
                int bytesRead = await _fs
                    .ReadAsync(buffer, 0, FrameSize, cancellationToken)
                    .ConfigureAwait(false);
                // any unread portion remains zero
            }
            finally
            {
                _streamLock.Release();
            }

            return buffer;
        }

        protected override async Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken)
        {
            long offset = checked(frameId * (long)FrameSize);

            await _streamLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _fs!.Seek(offset, SeekOrigin.Begin);
                await _fs
                    .WriteAsync(data, 0, FrameSize, cancellationToken)
                    .ConfigureAwait(false);
                await _fs
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _streamLock.Release();
            }
        }
    }
}
