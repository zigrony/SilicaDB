using System.Threading.Channels;
using System.Text;

using SilicaDB.Diagnostics.Tracing;

namespace SilicaDB.Diagnostics.Tracing.Sinks
{

    public sealed class FileTraceSink : ITraceSink
    {
        private readonly Channel<TraceEvent> _chan;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly StreamWriter _writer;

        public FileTraceSink(string directory, string prefix = "trace", int capacity = 8192)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
            { AutoFlush = false, NewLine = "\n" };

            _chan = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            _pump = Task.Run(PumpAsync);
        }

        public void Append(in TraceEvent evt)
        {
            // Non-blocking; drop on full
            _chan.Writer.TryWrite(evt);
        }

        private async Task PumpAsync()
        {
            var reader = _chan.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var e))
                    {
                        // TSV line (fast to parse)
                        await _writer.WriteLineAsync(
                            $"{e.Seq}\t{e.TsTicks}\t{e.Category}\t{e.Type}\t{e.Source}\t{e.Resource}\t{e.Message}\t{e.DurationMicros}\t{e.ThreadId}"
                        ).ConfigureAwait(false);
                    }
                    await _writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await _writer.FlushAsync().ConfigureAwait(false);
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask FlushAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            await _writer.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _chan.Writer.TryComplete();
            try { _pump.GetAwaiter().GetResult(); }
            catch { /* swallow */ }
            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _chan.Writer.TryComplete();
            try { await _pump.ConfigureAwait(false); }
            catch { /* swallow */ }
            _cts.Dispose();
        }
    }
}