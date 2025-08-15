// File: PhysicalBlockDevice.cs
// Namespace: SilicaDB.Devices

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices.Exceptions;
using SilicaDB.Devices.Interfaces;
using SilicaDB.Diagnostics.Tracing;

namespace SilicaDB.Devices
{
    /// <summary>
    /// A file-system backed block device. Creates the file if it does not exist.
    /// Uses positional, handle-based I/O (RandomAccess.ReadAsync/WriteAsync)
    /// to allow concurrent reads/writes without a global seek lock.
    /// </summary>
    public class PhysicalBlockDevice : AsyncStorageDeviceBase
    {
        private readonly string _path;
        private FileStream? _fs;

        public PhysicalBlockDevice(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {

            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"OnMountAsync",GetType().Name);
            // Open or create for asynchronous R/W
            _fs = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: FrameSize,
                options: FileOptions.Asynchronous);

            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {

            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"OnUnmountAsync",GetType().Name);
            // Dispose underlying FileStream and the global stream lock
            _fs?.Dispose();
            _fs = null;

            await Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(long frameId, CancellationToken cancellationToken)
        {

            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"ReadFrameInternalAsync",$"{GetType().Name}:{frameId}");

            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            long offset = checked(frameId * (long)FrameSize);
            long endExclusive = checked(offset + FrameSize);
            long lengthSnapshot = _fs.Length;

            if (endExclusive > lengthSnapshot)
                throw new DeviceReadOutOfRangeException(offset, FrameSize, lengthSnapshot);

            var buffer = new byte[FrameSize];
            int total = 0;

            // Loop until we have an exact, full frame read
            while (total < buffer.Length)
            {
                int n = await RandomAccess.ReadAsync(
                    _fs.SafeFileHandle,
                    buffer.AsMemory(total, buffer.Length - total),
                    offset + total,
                    cancellationToken).ConfigureAwait(false);

                if (n == 0)
                    throw new IOException($"Short read at offset {offset + total}, expected {buffer.Length - total} more bytes.");

                total += n;
            }

            return buffer;
        }

        protected override async Task WriteFrameInternalAsync(long frameId, byte[] data, CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"WriteFrameInternalAsync",$"{GetType().Name}:{frameId}");

            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            long offset = checked(frameId * (long)FrameSize);

            // positional write — no global lock needed
            await RandomAccess.WriteAsync(
                    _fs!.SafeFileHandle,
                    data,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);

            // ensure durability
            //await _fs!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"FlushAsync",GetType().Name);

            if (_fs is null)
                throw new InvalidOperationException("Device is not mounted");

            // ensure durability
            await _fs!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

    }
}
