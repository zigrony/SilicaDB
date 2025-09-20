// Filename: PageReadLease.cs
using System;
using System.Threading.Tasks;
using static Silica.BufferPool.BufferPoolManager;

namespace Silica.BufferPool
{
    // ─────────────────────────────────────────────────────────────
    // Leases
    // ─────────────────────────────────────────────────────────────

    public readonly struct PageReadLease : IAsyncDisposable
    {
        private readonly LeaseCore? _core;
        public readonly ReadOnlyMemory<byte> Page;

        internal PageReadLease(LeaseCore core, ReadOnlyMemory<byte> page)
        {
            _core = core;
            Page = page;
        }

        public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

}

