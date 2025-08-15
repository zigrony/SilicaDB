using System;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Diagnostics.Tracing;

namespace WalTestApp
{
    public sealed class ConsoleTraceSink : ITraceSink
    {
        public void Append(in TraceEvent e)
        {
            var line = $"[{e.UtcTicks / TimeSpan.TicksPerMillisecond} ms] "
                     + $"{e.Category,-10} {e.Type,-9} {e.Source}";

            if (e.DurationMicros > 0)
                line += $" (+{e.DurationMicros / 1000.0:F1} ms)";

            Console.WriteLine(line);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            // No buffering, so nothing to flush
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // No async resources to clean up
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            // No unmanaged resources
        }
    }
}
