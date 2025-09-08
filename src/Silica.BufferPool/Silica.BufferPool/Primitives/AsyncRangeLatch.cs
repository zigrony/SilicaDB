// Filename: AsyncRangeLatch.cs

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silica.Durability;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.BufferPool
{
    // ---------- AsyncRangeLatch ----------

    /// Coordinated per-page latch:
    /// - multiple readers if no writer
    /// - writers block readers and other overlapping writers
    /// - FIFO queues, writer preference
    /// </summary>
    internal sealed class AsyncRangeLatch : IDisposable, IAsyncDisposable
    {
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private int _activeReaders;
        private readonly List<RangeSeg> _activeWrites = new();
        private readonly Queue<ReaderWait> _readerQ = new();
        private readonly Queue<WriterWait> _writerQ = new();
        private bool _disposed;
        public bool IsDisposed => _disposed;

        public async Task<Releaser> AcquireReadAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            TaskCompletionSource<Releaser> tcs;
            ReaderWait waiter;

            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_activeWrites.Count == 0 && _writerQ.Count == 0)
                {
                    _activeReaders++;
                    // Metric: reader granted immediately
                    // BufferPoolMetrics.RecordLatchGranted(isWriter:false);
                    return new Releaser(this, isWriter: false, seg: default);
                }
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = new ReaderWait(tcs);
                _readerQ.Enqueue(waiter);
                // Metric: reader queued
                // BufferPoolMetrics.RecordLatchQueued(isWriter:false);
            }
            finally
            {
                _mutex.Release();
            }

            using var reg = ct.CanBeCanceled
                ? ct.Register(static s => ((ReaderWait)s!).TryCancel(), waiter)
                : default;

            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<Releaser> AcquireWriteAsync(
            int offset,
            int length,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            var seg = new RangeSeg(offset, offset + length);

            TaskCompletionSource<Releaser> tcs;
            WriterWait waiter;
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_activeReaders == 0 && !OverlapsAny(_activeWrites, seg))
                {
                    _activeWrites.Add(seg);
                    // Metric: writer granted immediately
                    // BufferPoolMetrics.RecordLatchGranted(isWriter:true);
                    return new Releaser(this, isWriter: true, seg);
                }
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = new WriterWait(seg, tcs);
                _writerQ.Enqueue(waiter);
                // Metric: writer queued
                // BufferPoolMetrics.RecordLatchQueued(isWriter:true);
            }
            finally
            {
                _mutex.Release();
            }

            // Register cancellation on the same TCS
            //using var reg = ct.CanBeCanceled
            //    ? ct.Register(static s => ((WriterWait)s!).TryCancel(), new WriterWait(seg, tcs))
            //    : default;
            using var reg = ct.CanBeCanceled
                  ? ct.Register(static s => ((WriterWait)s!).TryCancel(), waiter)
                  : default;
            return await tcs.Task.ConfigureAwait(false);
        }

        private static bool OverlapsAny(List<RangeSeg> list, RangeSeg seg)
        {
            foreach (var w in list)
                if (w.Overlaps(seg)) return true;
            return false;
        }

        public async ValueTask ReleaseAsync(bool isWriter, RangeSeg seg)
        {
            // Allow release even if disposed to prevent deadlocks during shutdown.
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (isWriter)
                {
                    _activeWrites.Remove(seg);
                }
                else
                {
                    _activeReaders--;
                    if (_activeReaders < 0)
                        throw new InvalidOperationException("Reader underflow");
                }

                Promote();
            }
            finally
            {
                _mutex.Release();
            }
        }

        private void Promote()
        {
            // Writers first if no readers
            if (_writerQ.Count > 0)
            {
                var snapshot = new List<RangeSeg>(_activeWrites);
                var remaining = new Queue<WriterWait>();
                bool grantedAny = false;

                while (_writerQ.Count > 0)
                {
                    var w = _writerQ.Dequeue();
                    if (w.IsCanceled)
                        continue;

                    if (!OverlapsAny(snapshot, w.Range))
                    {
                        _activeWrites.Add(w.Range);
                        snapshot.Add(w.Range);
                        w.Tcs.TrySetResult(new Releaser(this, isWriter: true, w.Range));
                        grantedAny = true;
                    }
                    else
                    {
                        remaining.Enqueue(w);
                    }
                }
                foreach (var w in remaining)
                    _writerQ.Enqueue(w);

                if (grantedAny)
                    return;   // do not wake readers this cycle
            }

            // Then readers if no writers waiting or active
            if (_activeWrites.Count == 0 && _writerQ.Count == 0 && _readerQ.Count > 0)
            {
                int granted = 0;
                // Wake all queued readers; only count those actually granted (not canceled)
                while (_readerQ.Count > 0)
                {
                    var r = _readerQ.Dequeue();
                    if (r.TryGrant(this))
                        granted++;
                }
                if (granted > 0)
                    _activeReaders += granted;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncRangeLatch));
        }

        // -------------------------------------------------------------
        // IDisposable / IAsyncDisposable to free the internal semaphore
        // -------------------------------------------------------------
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Deterministically drain queues and cancel waiters; keep semaphore alive to allow in-flight releases.
            try
            {
                _mutex.Wait();
                try
                {
                    // Cancel queued writers
                    while (_writerQ.Count > 0)
                    {
                        var w = _writerQ.Dequeue();
                        w.TryCancel();
                    }
                    // Fail queued readers
                    while (_readerQ.Count > 0)
                    {
                        var r = _readerQ.Dequeue();
                        r.TryCancel();
                    }
                }
                finally
                {
                    _mutex.Release();
                }
            }
            catch { /* best-effort during shutdown, waiters already prevented from being queued/granted */ }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await ValueTask.CompletedTask;
        }

        // ------------------------------------------------------------------
        // Nested helper types
        // ------------------------------------------------------------------

        private sealed class ReaderWait
        {
            public TaskCompletionSource<Releaser> Tcs { get; }
            private int _canceled;   // 0 = live, 1 = canceled

            public bool IsCanceled => _canceled == 1;

            public ReaderWait(TaskCompletionSource<Releaser> tcs)
            {
                Tcs = tcs;
            }

            public void TryCancel()
            {
                if (Interlocked.CompareExchange(ref _canceled, 1, 0) == 0)
                    Tcs.TrySetCanceled();
            }

            public bool TryGrant(AsyncRangeLatch owner)
            {
                // Grant succeeds only if not already canceled/completed.
                return Tcs.TrySetResult(new Releaser(owner, isWriter: false, seg: default));
            }
        }

        private sealed class WriterWait
        {
            public RangeSeg Range { get; }
            public TaskCompletionSource<Releaser> Tcs { get; }
            private int _canceled;   // 0 = live, 1 = canceled

            public bool IsCanceled => _canceled == 1;

            public WriterWait(RangeSeg range, TaskCompletionSource<Releaser> tcs)
            {
                Range = range;
                Tcs = tcs;
            }

            public void TryCancel()
            {
                if (Interlocked.CompareExchange(ref _canceled, 1, 0) == 0)
                    Tcs.TrySetCanceled();
            }
        }

        public readonly struct Releaser : IAsyncDisposable
        {
            private readonly AsyncRangeLatch _owner;
            private readonly bool _writer;
            private readonly RangeSeg _seg;

            internal Releaser(AsyncRangeLatch owner, bool isWriter, RangeSeg seg)
            {
                _owner = owner;
                _writer = isWriter;
                _seg = seg;
            }

            public ValueTask DisposeAsync()
                => _owner.ReleaseAsync(_writer, _seg);
        }

        public readonly struct RangeSeg : IEquatable<RangeSeg>
        {
            public int Start { get; }
            public int End { get; }

            public RangeSeg(int start, int end)
            {
                if (start < 0 || end <= start)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Start = start;
                End = end;
            }

            public bool Overlaps(RangeSeg other)
                => Start < other.End && other.Start < End;

            public bool Equals(RangeSeg other)
                => Start == other.Start && End == other.End;

            public override bool Equals(object? obj)
                => obj is RangeSeg r && Equals(r);

            public override int GetHashCode()
                => HashCode.Combine(Start, End);
        }
    }
}
