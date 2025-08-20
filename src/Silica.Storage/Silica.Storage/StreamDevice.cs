using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;   // For SafeFileHandle
using Silica.Storage;
using Silica.Storage.Exceptions;
using Silica.Observability.Tracing;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Devices
{
    public class StreamDevice : AsyncMiniDriver, IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _hasPositional;
        private readonly SafeFileHandle? _fileHandle;
        private readonly SemaphoreSlim _globalLock = new(1, 1);
        private readonly StorageGeometry _geometry;

        // Preferred: caller specifies geometry
        public StreamDevice(Stream stream, StorageGeometry geometry)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _geometry = geometry;

            if (stream is FileStream fs && !fs.IsAsync)
                throw new ArgumentException("FileStream must be opened with FileOptions.Asynchronous", nameof(stream));

            if (stream is FileStream fileStream)
            {
                _hasPositional = true;
                _fileHandle = fileStream.SafeFileHandle;
            }
        }

        // Convenience: default geometry (4096B)
        public StreamDevice(Stream stream)
            : this(stream, new StorageGeometry
            {
                LogicalBlockSize = 4096,
                MaxIoBytes = 1 << 20,
                RequiresAlignedIo = false,
                SupportsFua = false
            })
        { }

        public override StorageGeometry Geometry => _geometry;

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

        protected override async Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device,
                "ReadFrameInternalAsync",
                $"{GetType().Name}:{frameId}");

            var offset = checked(frameId * (long)Geometry.LogicalBlockSize);
            var endExclusive = checked(offset + Geometry.LogicalBlockSize);

            if (_hasPositional && _fileHandle is not null)
            {
                if (_stream is not FileStream fs)
                    throw new InvalidOperationException("Positional mode requires FileStream.");

                long lengthSnapshot = fs.Length;
                if (endExclusive > lengthSnapshot)
                    throw new DeviceReadOutOfRangeException(offset, Geometry.LogicalBlockSize, lengthSnapshot);

                int total = 0;
                while (total < buffer.Length)
                {
                    int n = await RandomAccess.ReadAsync(
                        _fileHandle,
                        buffer.Slice(total),
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
                    throw new DeviceReadOutOfRangeException(offset, Geometry.LogicalBlockSize, lengthSnapshot);

                await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;

                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int n = await _stream.ReadAsync(buffer.Slice(total), cancellationToken)
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
        }

        protected override async Task WriteFrameInternalAsync(
            long frameId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            await using var _scope = Trace.AsyncScope(
                TraceCategory.Device,
                "WriteFrameInternalAsync",
                $"{GetType().Name}:{frameId}");

            var offset = checked(frameId * (long)Geometry.LogicalBlockSize);

            if (_hasPositional && _fileHandle is not null)
            {
                await RandomAccess.WriteAsync(_fileHandle, data, offset, cancellationToken)
                                  .ConfigureAwait(false);
            }
            else
            {
                await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;
                    await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
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

        public override ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            // No EnsureMounted* in base; just flush the underlying stream.
            return _stream.FlushAsync(cancellationToken).AsValueTask();
        }
    }
}
