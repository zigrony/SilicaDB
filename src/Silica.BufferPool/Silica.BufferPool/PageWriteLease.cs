// Filename: PageWriteLease.cs

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
    // Leases
    // ─────────────────────────────────────────────────────────────

    public readonly struct PageWriteLease : IAsyncDisposable
    {
        private readonly Frame _frame;
        private readonly AsyncRangeLatch.Releaser _release;
        public readonly Memory<byte> Slice;

        internal PageWriteLease(Frame f, AsyncRangeLatch.Releaser r, Memory<byte> s)
        {
            _frame = f;
            _release = r;
            Slice = s;
        }

        public async ValueTask DisposeAsync()
        {
            await _release.DisposeAsync().ConfigureAwait(false);
            _frame.Unpin();
        }
    }
}
