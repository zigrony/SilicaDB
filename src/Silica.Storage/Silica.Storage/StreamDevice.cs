using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;   // For SafeFileHandle
using Silica.Storage;
using Silica.Storage.Exceptions;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.DiagnosticsCore.Extensions.Storage; // StorageMetrics


namespace Silica.Storage.Devices
{
    public class StreamDevice : AsyncMiniDriver, IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _hasPositional;
        private readonly bool _ownsStream;
        private readonly SafeFileHandle? _fileHandle;
        private readonly SemaphoreSlim _globalLock = new(1, 1);
        private readonly StorageGeometry _geometry;
        private int _lockDisposed; // 0 = not disposed, 1 = disposed

        // DiagnosticsCore handled by AsyncMiniDriver base

        // Preferred: caller specifies geometry
        public StreamDevice(Stream stream, StorageGeometry geometry, bool ownsStream = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _geometry = geometry;
            _ownsStream = ownsStream;

            if (stream is FileStream fs && !fs.IsAsync)
                throw new ArgumentException("FileStream must be opened with FileOptions.Asynchronous", nameof(stream));

            // Require basic capabilities up-front to fail fast (enterprise contract)
            if (!_stream.CanRead) throw new NotSupportedException("Stream must be readable.");
            if (!_stream.CanWrite) throw new NotSupportedException("Stream must be writable.");

            if (stream is FileStream fileStream)
            {
                _hasPositional = true;
                _fileHandle = fileStream.SafeFileHandle;
            }
            else
            {
                // Non-positional mode requires seekability for strict offset I/O
                if (!_stream.CanSeek)
                    throw new NotSupportedException("Non-file streams must be seekable for strict offset I/O.");
            }
        }

        // Convenience: default geometry (4096B)
        public StreamDevice(Stream stream, bool ownsStream = false)
            : this(stream, new StorageGeometry
            {
                LogicalBlockSize = 4096,
                MaxIoBytes = 1 << 20,
                RequiresAlignedIo = false,
                SupportsFua = false
            }, ownsStream)
        { }

        public override StorageGeometry Geometry => _geometry;

        protected override async Task OnMountAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        protected override async Task OnUnmountAsync(CancellationToken cancellationToken)
        {

            await Task.CompletedTask;
        }

        protected override async Task ReadFrameInternalAsync(
            long frameId,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
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
                        throw new DeviceReadOutOfRangeException(
                            offset: offset + total,
                            requestedLength: buffer.Length - total,
                            deviceLength: lengthSnapshot);

                    total += n;
                }
            }
            else
            {
                if (!_stream.CanSeek)
                    throw new NotSupportedException("Strict reads require a seekable stream.");

                await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    long lengthSnapshot = _stream.Length;
                    if (endExclusive > lengthSnapshot)
                        throw new DeviceReadOutOfRangeException(offset, Geometry.LogicalBlockSize, lengthSnapshot);

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
                    // Do not flush per write; durability is provided by FlushAsync/Unmount
                }
                finally
                {
                    _globalLock.Release();
                }
            }
        }

        public void Dispose()
        {
            // Bridge to async dispose to ensure lifecycle-consistent teardown.
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        protected override Task FlushAsyncInternal(CancellationToken cancellationToken)
        {
            return _stream.FlushAsync(cancellationToken);
        }

        // Ensure semaphore gets disposed when the device is disposed via IAsyncDisposable.
        // We do NOT dispose in OnUnmountAsync so the device can be mounted again if desired.
        public override async ValueTask DisposeAsync()
        {
            // Drain, unmount, and base teardown
            await base.DisposeAsync().ConfigureAwait(false);
            // Now it's safe to dispose the lock (no I/O remains in-flight)
            TryDisposeLockOnce();

            // Optionally dispose the underlying stream if we own it
            if (_ownsStream)
            {
                try
                {
                    // Prefer async dispose when available without reflection
                    if (_stream is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _stream.Dispose();
                    }
                }
                catch
                {
                }
            }

        }

        private void TryDisposeLockOnce()
        {
            if (Interlocked.Exchange(ref _lockDisposed, 1) == 0)
            {
                try { _globalLock.Dispose(); } catch { /* swallow */ }
            }
        }

    }
}
