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

            return Task.CompletedTask;
        }

    protected override async Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken)
    {
        var buffer = new byte[FrameSize];
        long offset = checked(frameId * (long)FrameSize);

        // if beyond current EOF, return zeros
        if (offset >= _fs!.Length)
            return buffer;

        // positional read — no shared pointer, no global lock
        int bytesRead = await RandomAccess.ReadAsync(
                _fs.SafeFileHandle,
                buffer,
                offset,
                cancellationToken)
            .ConfigureAwait(false);

        // any unread portion is already zero
        return buffer;
    }

        protected override async Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken)
        {
            long offset = checked(frameId * (long)FrameSize);

            // positional write — no global lock needed
            await RandomAccess.WriteAsync(
                    _fs!.SafeFileHandle,
                    data,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);

            // ensure durability
            await _fs!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
