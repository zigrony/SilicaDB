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

        public readonly SemaphoreSlim LoadGate = new(1, 1);
        private byte[]? _bytes;

        public readonly AsyncRangeLatch Latch = new();

        public Memory<byte> Buffer =>
            (_bytes ?? throw new InvalidOperationException("Buffer uninitialized"))
            .AsMemory();

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
            if (_bytes is null)
                _bytes = ArrayPool<byte>.Shared.Rent(size);
        }

        internal void ReturnBuffer()
        {
            if (_bytes != null)
            {
                ArrayPool<byte>.Shared.Return(_bytes, clearArray: true);
                _bytes = null;
            }
        }

        public void MarkLoaded() => Volatile.Write(ref _loadState, 2);

        public int PinCount => Volatile.Read(ref _pinCount);

        public Task PinAsync(CancellationToken _)
        {
            Interlocked.Increment(ref _pinCount);
            return Task.CompletedTask;
        }

        public void Unpin()
        {
            if (Interlocked.Decrement(ref _pinCount) < 0)
                throw new InvalidOperationException("Unbalanced pin/unpin.");
        }

        public bool IsDirty => Volatile.Read(ref _dirty);
        public void MarkDirty() => Volatile.Write(ref _dirty, true);
        public void ClearDirty() => Volatile.Write(ref _dirty, false);
    }

}
