// Filename: PageWriteLease.cs
using System;
using System.Threading.Tasks;
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

        /// <summary>
        /// Explicitly mark the page dirty with the associated page LSN
        /// after you have appended the corresponding log record(s).
        /// Must be called while the write lease is still held.
        /// </summary>
        public void MarkDirty(long pageLsn)
            => _core?.MarkDirty(pageLsn);
    }
}
