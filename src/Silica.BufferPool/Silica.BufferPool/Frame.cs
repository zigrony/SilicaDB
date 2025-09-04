// Filename: Frame.cs

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Durability;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Extensions.BufferPool;
using static Silica.BufferPool.BufferPoolManager;

namespace Silica.BufferPool
{
    // ─────────────────────────────────────────────────────────────
    // Frame: per-page state
    // ─────────────────────────────────────────────────────────────

    internal sealed class Frame
    {
        private int _pinCount;
        private volatile bool _dirty;
        private int _loadState; // 0=none,1=loading-or-loaded,2=loaded
        private int _size;      // logical page size exposed via Buffer

        public readonly SemaphoreSlim LoadGate = new(1, 1);
        private byte[]? _bytes;

        public readonly AsyncRangeLatch Latch = new();

        public Memory<byte> Buffer =>
            (_bytes ?? throw new InvalidOperationException("Buffer uninitialized"))
            .AsMemory(0, _size);

        public bool Loaded => Volatile.Read(ref _loadState) == 2;

        public bool TryMarkLoadingOrLoaded(out bool alreadyLoaded)
        {
            var s = Volatile.Read(ref _loadState);
            if (s == 2) { alreadyLoaded = true; return true; }
            if (s == 1) { alreadyLoaded = false; return true; }
            alreadyLoaded = false;
            return Interlocked.CompareExchange(ref _loadState, 1, 0) == 0;
        }

        public void InitializeBuffer(int size)
        {
            // Idempotent and size-correct: if a buffer exists with wrong size, return and rent correct size.
            var current = _bytes;
            if (current is null)
            {
                var rented = ArrayPool<byte>.Shared.Rent(size);
                Volatile.Write(ref _bytes, rented);
                _size = size;
                return;
            }
            if (_size != size)
            {
                ArrayPool<byte>.Shared.Return(current, clearArray: true);
                var rented = ArrayPool<byte>.Shared.Rent(size);
                Volatile.Write(ref _bytes, rented);
                _size = size;
            }
        }

        internal void ReturnBuffer()
        {
            if (_bytes != null)
            {
                ArrayPool<byte>.Shared.Return(_bytes, clearArray: true);
                _bytes = null;
                _size = 0;
                // Reset state for safety if the frame instance lingers after eviction.
                Volatile.Write(ref _loadState, 0);
                Volatile.Write(ref _dirty, false);
            }
        }

        public void MarkLoaded() => Volatile.Write(ref _loadState, 2);

        public int PinCount => Volatile.Read(ref _pinCount);

        public ValueTask PinAsync(CancellationToken _)
        {
            Interlocked.Increment(ref _pinCount);
            return ValueTask.CompletedTask;
        }

        public void Unpin()
        {
            var newCount = Interlocked.Decrement(ref _pinCount);
            if (newCount < 0)
                throw new InvalidOperationException("Unbalanced pin/unpin.");
            // Optional: metric hook for pin/unpin tracking
            // BufferPoolMetrics.RecordUnpinned(_metrics);
        }

        public bool IsDirty => Volatile.Read(ref _dirty);
        public void MarkDirty() => Volatile.Write(ref _dirty, true);
        public void ClearDirty() => Volatile.Write(ref _dirty, false);
    }

}
