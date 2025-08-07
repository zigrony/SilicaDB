using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SilicaDB.Logging
{
    public class SilicaLogger : IAsyncDisposable
    {
        private enum State { Running = 0, ShuttingDown = 1, Closed = 2 }

        private readonly LogLevel _minLevel;
        private readonly Channel<LogEntry> _channel;
        private readonly StreamWriter _fileWriter;
        private readonly bool _toConsole;

        // Control state transitions and cancellation of the reader loop
        private int _state;
        private readonly CancellationTokenSource _readerCts = new();

        // The background drain task
        private readonly Task _drainTask;

        public SilicaLogger(string filePath, LogLevel minLevel = LogLevel.Trace, bool toConsole = false)
        {
            _minLevel = minLevel;
            _toConsole = toConsole;
            _fileWriter = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };

            _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Kick off the background loop, honouring _readerCts for forced abort.
            _drainTask = Task.Run(() => ProcessQueueAsync(_readerCts.Token));
        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            // Only allow while Running
            if (Interlocked.CompareExchange(ref _state, 0, 0) != (int)State.Running)
                throw new ObjectDisposedException(nameof(SilicaLogger));

            if (level < _minLevel) return;

            var text = args?.Length > 0
                ? string.Format(message, args)
                : message;

            var entry = new LogEntry(DateTime.UtcNow, level, text);

            // This should always succeed in Running state
            _channel.Writer.TryWrite(entry);
        }

        private async Task ProcessQueueAsync(CancellationToken cancel)
        {
            try
            {
                // This will throw if 'cancel' is signaled
                await foreach (var entry in _channel.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
                {
                    var line = $"{entry.Timestamp:O} [{entry.Level}] {entry.Message}";
                    _fileWriter.WriteLine(line);
                    if (_toConsole) Console.WriteLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                // Forced abort – swallow and exit
            }
        }

        /// <summary>
        /// Gracefully flush then force‐abort on token fire.
        /// </summary>
        public async Task ShutdownAsync(CancellationToken gracefulWaitToken)
        {
            // Transition to ShuttingDown (only once)
            if (Interlocked.Exchange(ref _state, (int)State.ShuttingDown) != (int)State.Running)
                throw new ObjectDisposedException(nameof(SilicaLogger));

            // Stop accepting new entries
            _channel.Writer.Complete();

            // Wait for either full drain or gracefulWaitToken
            var waitTask = _drainTask;
            var delayTask = Task.Delay(Timeout.Infinite, gracefulWaitToken);

            var finished = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
            if (finished == delayTask)
            {
                // Graceful window elapsed – force‐abort remaining work
                _readerCts.Cancel();
                try
                {
                    await waitTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* expected */ }

                // Let caller know we couldn’t finish in time
                throw new OperationCanceledException("Logger shutdown timed out.", gracefulWaitToken);
            }

            // Drained normally
            await waitTask.ConfigureAwait(false);

            // Transition to Closed and clean up
            FinalizeShutdown();
        }

        public async ValueTask DisposeAsync()
        {
            // If still Running, just do a normal graceful shutdown with infinite wait
            if (Interlocked.Exchange(ref _state, (int)State.ShuttingDown) == (int)State.Running)
            {
                _channel.Writer.Complete();
                _readerCts.CancelAfter(TimeSpan.FromHours(1));  // “practically infinite”
                try { await _drainTask.ConfigureAwait(false); }
                catch { /* ignore */ }
            }
            FinalizeShutdown();
        }

        private void FinalizeShutdown()
        {
            if (Interlocked.Exchange(ref _state, (int)State.Closed) == (int)State.Closed)
                return;

            _fileWriter.Dispose();
            _readerCts.Dispose();
        }

        private record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
    }
}
