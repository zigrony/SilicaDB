using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;   // For SafeFileHandle
using SilicaDB.Devices;
using SilicaDB.Devices.Exceptions;
using SilicaDB.Diagnostics.Tracing;


namespace SilicaDB.Devices
{
    public class StreamDevice : AsyncStorageDeviceBase, IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _hasPositional;
        private readonly SafeFileHandle? _fileHandle;
        private readonly SemaphoreSlim _globalLock = new(1, 1);

        public StreamDevice(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));

            // Enforce async FileStream if you want true positional I/O
            if (stream is FileStream fs && !fs.IsAsync)
                throw new ArgumentException(
                    "FileStream must be opened with FileOptions.Asynchronous", nameof(stream));

            if (stream is FileStream fileStream)
            {
                _hasPositional = true;
                _fileHandle = fileStream.SafeFileHandle;
            }
        }

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device,
                "OnMountAsync",
                GetType().Name);
        
            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device,
                "OnUnmountAsync",
                GetType().Name);

            await Task.CompletedTask;
        }

        protected override async Task<byte[]> ReadFrameInternalAsync(
            long frameId,
            CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"ReadFrameInternalAsync",$"{GetType().Name}:{frameId}");
            
            var offset = checked(frameId * (long)FrameSize);
            var endExclusive = checked(offset + FrameSize);
            var buffer = new byte[FrameSize];

            if (_hasPositional && _fileHandle is not null)
            {
                // We can snapshot length from the underlying FileStream
                if (_stream is not FileStream fs)
                    throw new InvalidOperationException("Positional mode requires FileStream.");

                long lengthSnapshot = fs.Length;
                if (endExclusive > lengthSnapshot)
                    throw new DeviceReadOutOfRangeException(offset, FrameSize, lengthSnapshot);

                int total = 0;
                while (total < buffer.Length)
                {
                    int n = await RandomAccess.ReadAsync(
                        _fileHandle,
                        buffer.AsMemory(total, buffer.Length - total),
                        offset + total,
                        cancellationToken).ConfigureAwait(false);

                    if (n == 0)
                        throw new IOException($"Short read at offset {offset + total}, expected {buffer.Length - total} more bytes.");

                    total += n;
                }
            }
            else
            {
                if (!_stream.CanSeek)
                    throw new NotSupportedException("Strict reads require a seekable stream.");

                long lengthSnapshot = _stream.Length;
                if (endExclusive > lengthSnapshot)
                    throw new DeviceReadOutOfRangeException(offset, FrameSize, lengthSnapshot);

                await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int n = await _stream
                            .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                            .ConfigureAwait(false);

                        if (n == 0)
                            throw new IOException($"Short read at position {_stream.Position}, expected {buffer.Length - total} more bytes.");

                        total += n;
                    }
                }
                finally
                {
                    _globalLock.Release();
                }
            }

            return buffer;
        }

        protected override async Task WriteFrameInternalAsync(
            long frameId,
            byte[] data,
            CancellationToken cancellationToken)
        {

            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"WriteFrameInternalAsync",$"{GetType().Name}:{frameId}");
            
            var offset = frameId * FrameSize;

            // Clone so callers can reuse their buffer
            var frame = new byte[FrameSize];
            Buffer.BlockCopy(data, 0, frame, 0, FrameSize);

            if (_hasPositional && _fileHandle is not null)
            {
                // .NET 6+ positional write
                await RandomAccess
                    .WriteAsync(_fileHandle, frame, offset, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // fallback: serialize Seek+WriteAsync
                await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;
                    await _stream.WriteAsync(frame, 0, frame.Length, cancellationToken)
                                 .ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _globalLock.Release();
                }
            }
        }

        public void Dispose()
        {
            _globalLock.Dispose();
            // do not Dispose(_stream) here; caller owns it
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(TraceCategory.Device,"FlushAsync",GetType().Name);
            
            EnsureMountedOrThrow();

            if (_stream is null)
                throw new InvalidOperationException("Device is not mounted");

            // ensure durability
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

    }
}
