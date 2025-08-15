using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SilicaDB.Diagnostics.Tracing;


namespace SilicaDB.Diagnostics.Tracing
{

    public static class Trace
    {
        private static ImmutableArray<ITraceSink> _sinks = ImmutableArray<ITraceSink>.Empty;
        private static long _nextSeq = 0;

        // Simple filters (expand later if needed)
        public static volatile bool Enabled = true;
        private static volatile int _categoryMask = ~0; // all on

        public static void EnableCategories(params TraceCategory[] cats)
        {
            int mask = 0;
            foreach (var c in cats) mask |= 1 << (int)c;
            _categoryMask = mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEnabled(TraceCategory cat) => Enabled && ((_categoryMask & (1 << (int)cat)) != 0);

        public static IDisposable RegisterSink(ITraceSink sink)
        {
            ImmutableInterlocked.Update(ref _sinks, s => s.Add(sink));
            return new Unsubscriber(sink);
        }

        public static void UnregisterSink(ITraceSink sink)
        {
            ImmutableInterlocked.Update(ref _sinks, s => s.Remove(sink));
            sink.Dispose();
        }

        private sealed class Unsubscriber : IDisposable
        {
            private ITraceSink? _sink;
            public Unsubscriber(ITraceSink sink) => _sink = sink;
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _sink, null);
                if (s != null) UnregisterSink(s);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Emit(TraceEvent evt)
        {
            if (!Enabled || !IsEnabled(evt.Category)) return;

            var seq = Interlocked.Increment(ref _nextSeq);
            var e = evt with { Seq = seq };
            var sinks = _sinks; // snapshot to avoid races
            for (int i = 0; i < sinks.Length; i++)
            {
                sinks[i].Append(e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Info(TraceCategory cat, string src, string? msg = null, string? res = null)
        {
            if (!IsEnabled(cat)) return;
            Emit(TraceEvent.New(cat, TraceEventType.Info, src, res, msg));
        }

        public static IDisposable Scope(TraceCategory cat, string src, string? res = null)
            => new TraceScope(cat, src, res);

        public static IAsyncDisposable AsyncScope(TraceCategory cat, string src, string? res = null)
            => new AsyncTraceScope(cat, src, res);

        private readonly struct TraceScope : IDisposable
        {
            private readonly TraceCategory _cat;
            private readonly string _src, _res;
            private readonly long _t0;
            public TraceScope(TraceCategory cat, string src, string? res)
            {
                _cat = cat; _src = src; _res = res ?? "";
                _t0 = Stopwatch.GetTimestamp();
                if (IsEnabled(cat))
                    Emit(TraceEvent.New(cat, TraceEventType.Started, src, _res));
            }
            public void Dispose()
            {
                if (!IsEnabled(_cat)) return;
                var micros = (int)((Stopwatch.GetTimestamp() - _t0) * 1_000_000.0 / Stopwatch.Frequency);
                Emit(TraceEvent.New(_cat, TraceEventType.Completed, _src, _res, durMicros: micros));
            }
        }

        private sealed class AsyncTraceScope : IAsyncDisposable
        {
            private readonly TraceCategory _cat;
            private readonly string _src, _res;
            private readonly long _t0;
            public AsyncTraceScope(TraceCategory cat, string src, string? res)
            {
                _cat = cat; _src = src; _res = res ?? "";
                if (IsEnabled(cat))
                {
                    _t0 = Stopwatch.GetTimestamp();
                    Emit(TraceEvent.New(cat, TraceEventType.Started, src, _res));
                }
            }
            public ValueTask DisposeAsync()
            {
                if (!IsEnabled(_cat)) return ValueTask.CompletedTask;
                var micros = (int)((Stopwatch.GetTimestamp() - _t0) * 1_000_000.0 / Stopwatch.Frequency);
                Emit(TraceEvent.New(_cat, TraceEventType.Completed, _src, _res, durMicros: micros));
                return ValueTask.CompletedTask;
            }
        }
    }
}