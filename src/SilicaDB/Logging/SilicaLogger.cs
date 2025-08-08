// File: Logging/SilicaLogger.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SilicaDB.Metrics;

namespace SilicaDB.Logging
{
    public class SilicaLogger : IAsyncDisposable
    {
        private enum State { Running = 0, ShuttingDown = 1, Closed = 2 }

        private readonly LogLevel _minLevel;
        private readonly Channel<LogEntry> _channel;
        private readonly StreamWriter _fileWriter;
        private readonly bool _toConsole;

        private readonly IMetricsManager _metrics;
        private readonly bool _ownsMetrics;

        // Control state transitions and cancellation of the reader loop
        private int _state;
        private readonly CancellationTokenSource _readerCts = new();

        // The background drain task
        private readonly Task _drainTask;

        // Primary ctor: inject metrics manager
        public SilicaLogger(
            string filePath,
            IMetricsManager metrics,
            LogLevel minLevel = LogLevel.Trace,
            bool toConsole = false)
        {
            _minLevel = minLevel;
            _toConsole = toConsole;
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _ownsMetrics = false;

            RegisterMetrics(_metrics);

            _fileWriter = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };

            _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _drainTask = Task.Run(() => ProcessQueueAsync(_readerCts.Token));
        }

        // Convenience ctor: create and own a MetricsManager
        public SilicaLogger(
            string filePath,
            LogLevel minLevel = LogLevel.Trace,
            bool toConsole = false)
            : this(filePath, new MetricsManager(), minLevel, toConsole)
        {
            _ownsMetrics = true;
        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            // Only allow while Running
            if (Interlocked.CompareExchange(ref _state, 0, 0) != (int)State.Running)
                throw new ObjectDisposedException(nameof(SilicaLogger));

            // Filtered by level => dropped
            if (level < _minLevel)
            {
                _metrics.Increment(LogMetrics.Dropped.Name);
                return;
            }

            string text;
            // Measure formatting latency when args are provided
            if (args?.Length > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                text = string.Format(message, args);
                RecordMicros(LogMetrics.FormatLatencyMicros.Name, t0);
            }
            else
            {
                text = message;
            }

            var entry = new LogEntry(DateTime.UtcNow, level, text);

            // Emitted counter by severity bucket
            IncrementSeverity(level);

            // Try to enqueue; if it ever fails (e.g., shutting down), count as dropped
            if (!_channel.Writer.TryWrite(entry))
            {
                _metrics.Increment(LogMetrics.Dropped.Name);
            }
        }

        private async Task ProcessQueueAsync(CancellationToken cancel)
        {
            try
            {
                await foreach (var entry in _channel.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
                {
                    var line = $"{entry.Timestamp:O} [{entry.Level}] {entry.Message}";

                    long t0 = Stopwatch.GetTimestamp();
                    _fileWriter.WriteLine(line);
                    if (_toConsole) Console.WriteLine(line);
                    RecordMicros(LogMetrics.FlushLatencyMicros.Name, t0);
                }
            }
            catch (OperationCanceledException)
            {
                // Forced abort — swallow and exit
            }
        }

        /// <summary>
        /// Gracefully flush then force-abort on token fire.
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
                // Graceful window elapsed — force-abort remaining work
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
            // If still Running, just do a normal graceful shutdown with long wait
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
            if (_ownsMetrics && _metrics is IDisposable d)
                d.Dispose();
        }

        private void IncrementSeverity(LogLevel level)
        {
            switch (level)
            {
                // Bucket Trace/Debug/Information as INFO
                case LogLevel.Trace:
                case LogLevel.Debug:
                case LogLevel.Information:
                    _metrics.Increment(LogMetrics.EmittedInfo.Name);
                    break;

                case LogLevel.Warning:
                    _metrics.Increment(LogMetrics.EmittedWarning.Name);
                    break;

                // Bucket Error/Critical as ERROR
                case LogLevel.Error:
                case LogLevel.Critical:
                    _metrics.Increment(LogMetrics.EmittedError.Name);
                    break;
            }
        }

        private void RecordMicros(string metricName, long startTimestamp)
        {
            double micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency;
            _metrics.Record(metricName, micros);
        }

        private static void RegisterMetrics(IMetricsManager mgr)
        {
            // Same pattern as StorageMetrics/WalMetrics.RegisterAll
            mgr.Register(LogMetrics.EmittedInfo);
            mgr.Register(LogMetrics.EmittedWarning);
            mgr.Register(LogMetrics.EmittedError);
            mgr.Register(LogMetrics.Dropped);
            mgr.Register(LogMetrics.FlushLatencyMicros);
            mgr.Register(LogMetrics.FormatLatencyMicros);
        }

        private record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
    }
}
