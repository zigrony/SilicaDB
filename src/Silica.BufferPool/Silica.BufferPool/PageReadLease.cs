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

