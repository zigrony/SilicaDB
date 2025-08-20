using Silica.Storage.Interfaces;

namespace Silica.Storage
{
    public sealed class FaultInjectionDevice : IStorageDevice
    {
        private readonly IStorageDevice _inner;
        private readonly double _faultRate;
        private readonly Random _rng = new();

        public FaultInjectionDevice(IStorageDevice inner, double faultRate = 0.05)
        {
            _inner = inner;
            _faultRate = faultRate;
        }

        public StorageGeometry Geometry => _inner.Geometry;

        private void MaybeThrow()
        {
            if (_rng.NextDouble() < _faultRate)
                throw new IOException("Injected fault for testing.");
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            MaybeThrow();
            var bytesRead = await _inner.ReadAsync(offset, destination, ct).ConfigureAwait(false);

            // Corrupt data with given probability
            if (_rng.NextDouble() < _faultRate && bytesRead > 0)
                destination.Span.Slice(0, bytesRead).Fill(0xFF);

            return bytesRead;
        }

        public async ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> destination, CancellationToken ct = default)
        {
            MaybeThrow();
            var bytesRead = await _inner.ReadFrameAsync(frameId, destination, ct).ConfigureAwait(false);

            if (_rng.NextDouble() < _faultRate && bytesRead > 0)
                destination.Span.Slice(0, bytesRead).Fill(0xFF);

            return bytesRead;
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            MaybeThrow();
            await _inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            MaybeThrow();
            await _inner.WriteFrameAsync(frameId, source, ct).ConfigureAwait(false);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}