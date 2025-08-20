using Silica.Storage.Interfaces;

namespace Silica.Storage
{
    public sealed class LatencyJitterDevice : IStorageDevice
    {
        private readonly IStorageDevice _inner;
        private readonly TimeSpan _minDelay, _maxDelay;
        private readonly Random _rng = new();

        public LatencyJitterDevice(IStorageDevice inner, TimeSpan minDelay, TimeSpan maxDelay)
        {
            _inner = inner;
            _minDelay = minDelay;
            _maxDelay = maxDelay;
        }

        public StorageGeometry Geometry => _inner.Geometry;

        private Task DelayAsync(CancellationToken ct)
        {
            var jitterMs = _minDelay.TotalMilliseconds +
                           _rng.NextDouble() * (_maxDelay - _minDelay).TotalMilliseconds;
            return Task.Delay(TimeSpan.FromMilliseconds(jitterMs), ct);
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            await DelayAsync(ct).ConfigureAwait(false);
            return await _inner.ReadAsync(offset, destination, ct).ConfigureAwait(false);
        }

        public async ValueTask<int> ReadFrameAsync(long frameId, Memory<byte> destination, CancellationToken ct = default)
        {
            await DelayAsync(ct).ConfigureAwait(false);
            return await _inner.ReadFrameAsync(frameId, destination, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            await DelayAsync(ct).ConfigureAwait(false);
            await _inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
        }

        public async ValueTask WriteFrameAsync(long frameId, ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            await DelayAsync(ct).ConfigureAwait(false);
            await _inner.WriteFrameAsync(frameId, source, ct).ConfigureAwait(false);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}