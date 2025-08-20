using System.Collections.Concurrent;
using Silica.Observability.Tracing;

namespace Silica.Observability.Tracing.Sinks
{

    public sealed class BoundedTraceBuffer : ITraceSink
    {
        private readonly TraceEvent[] _ring;
        private long _startSeq;       // oldest seq that could be present
        private long _nextWriteSeq;   // next seq that will be written (for drop stats)
        private int _writeIdx;
        private readonly object _gate = new();

        public BoundedTraceBuffer(int capacity = 8192) => _ring = new TraceEvent[capacity];

        public void Append(in TraceEvent evt)
        {
            lock (_gate)
            {
                var idx = _writeIdx++ % _ring.Length;
                if (_nextWriteSeq > 0 && evt.Seq > _startSeq + _ring.Length) _startSeq = evt.Seq - _ring.Length + 1;
                _nextWriteSeq = evt.Seq + 1;
                _ring[idx] = evt;
            }
        }

        public IReadOnlyList<TraceEvent> Snapshot(int maxCount = 1024)
        {
            var list = new List<TraceEvent>(Math.Min(maxCount, _ring.Length));
            lock (_gate)
            {
                var newestSeq = _nextWriteSeq - 1;
                var first = Math.Max(_startSeq, newestSeq - Math.Min(maxCount, _ring.Length) + 1);
                for (long s = first; s <= newestSeq; s++)
                {
                    var idx = (int)(s % _ring.Length);
                    var e = _ring[idx];
                    if (e.Seq == s) list.Add(e);
                }
            }
            return list;
        }

        // <— ADD THIS:
        /// <summary>
        /// Drops all buffered events so that subsequent Snapshot() calls see an empty buffer.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                // Drop everything up to nextWriteSeq
                _startSeq = _nextWriteSeq;
                // Reset the ring pointer so evt.Seq % Length 
                // and writeIdx % Length stay in sync
                _writeIdx = (int)(_startSeq % _ring.Length);
            }
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}