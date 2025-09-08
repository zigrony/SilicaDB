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
using static Silica.BufferPool.BufferPoolManager;

namespace Silica.BufferPool
{
    // ─────────────────────────────────────────────────────────────
    // Leases
    // ─────────────────────────────────────────────────────────────

    public readonly struct PageWriteLease : IAsyncDisposable
    {
        private readonly LeaseCore? _core;
        public readonly Memory<byte> Slice;

        internal PageWriteLease(LeaseCore core, Memory<byte> slice)
        {
            _core = core;
            Slice = slice;
        }

        public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

}
