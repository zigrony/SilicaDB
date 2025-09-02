// Filename: PageReadLease.cs

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

namespace Silica.BufferPool
{
        // ─────────────────────────────────────────────────────────────
        // Leases
        // ─────────────────────────────────────────────────────────────

        public readonly struct PageReadLease : IAsyncDisposable
        {
            private readonly Frame _frame;
            private readonly AsyncRangeLatch.Releaser _release;
            public readonly ReadOnlyMemory<byte> Page;

            internal PageReadLease(Frame f, AsyncRangeLatch.Releaser r, Memory<byte> p)
            {
                _frame = f;
                _release = r;
                Page = p;
            }

            public async ValueTask DisposeAsync()
            {
                await _release.DisposeAsync().ConfigureAwait(false);
                _frame.Unpin();
            }
        }
    }
