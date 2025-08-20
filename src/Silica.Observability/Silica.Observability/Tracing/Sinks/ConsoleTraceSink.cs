using System.Diagnostics;
using Silica.Observability.Tracing;
namespace Silica.Observability.Tracing.Sinks
{

    public sealed class ConsoleTraceSink : ITraceSink
    {
        private readonly object _gate = new();

        public void Append(in TraceEvent evt)
        {
            var micros = evt.DurationMicros.HasValue ? $"{evt.DurationMicros}µs" : "";
            lock (_gate)
            {
                Console.WriteLine(
                    $"{evt.Seq,8} {evt.Category,-7} {evt.Type,-9} {evt.Source,-24} {micros,-8} tid={evt.ThreadId} res={evt.Resource} msg={evt.Message}"
                );
            }
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
