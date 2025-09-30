using System;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.IO;
using Silica.DiagnosticsCore.Metrics;
using static Silica.DiagnosticsCore.DiagnosticsOptions;

namespace Silica.DiagnosticsCore.Tracing
{
    public sealed class ConsoleTraceSink : ITraceSink, IDisposable, IAsyncDisposable
    {
        private readonly string _minLevel;
        private readonly IMetricsManager? _metrics;
        private readonly TextWriter _writer;
        private readonly bool _ownsWriter;

        // Internal non-blocking queue and background writer
        private readonly Channel<string> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private int _disposed; // idempotent disposal guard


        // Simple counters (self-observability)
        private long _droppedCount;
        private int _queueCount;
        private long _messagesWritten;
        private long _writeErrors;
        private volatile int _isActive;
        private double _lastWriteLatencyMs;
        private readonly int _shutdownDrainTimeoutMs;
        private readonly int _maxTagValueLen;
        private readonly int _maxWriteLatencyMs; // write latency guard (ms), protects against slow sinks
        // Note: DropOldest is coerced to DropNewest to avoid unobservable channel-eviction drops.
        // Basic circuit-breaker / backoff for persistent write failures
        private int _consecutiveWriteErrors;
        private long _circuitOpenUntilTicks; // DateTime.UtcNow.Ticks until which we skip writes (drop + metrics)

        // Backpressure policy
        public enum DropPolicy
        {
            DropNewest = 0,       // never block producers; new writes dropped when full
            DropOldest = 1,       // evict oldest when full
            BlockWithTimeout = 2  // bounded wait before dropping
        }

        private readonly DropPolicy _dropPolicy;
        private readonly TimeSpan _blockTimeout;

        // Formatting toggles
        private bool _fmtIncludeTags;
        private bool _fmtIncludeException;

        public ConsoleTraceSink WithFormatting(bool includeTags, bool includeException)
        {
            _fmtIncludeTags = includeTags;
            _fmtIncludeException = includeException;
            return this;
        }

        public ConsoleTraceSink(
            string minLevel,
            IMetricsManager? metrics = null,
            DropPolicy dropPolicy = DropPolicy.DropNewest,
            TimeSpan? blockTimeout = null,
            int? queueCapacity = null,
            int? shutdownDrainTimeoutMs = null, // configurable drain timeout
            TextWriter? writer = null,          // pluggable writer (defaults to Console.Out)
            int? maxWriteLatencyMs = null)      // optional latency guard; defaults to 2000ms
        {
            _minLevel = (minLevel ?? "trace").Trim().ToLowerInvariant();
            _metrics = metrics;
            _dropPolicy = dropPolicy;
            _blockTimeout = blockTimeout ?? TimeSpan.FromMilliseconds(10);
            _writer = writer ?? Console.Out;
            _ownsWriter = writer is not null
                && !object.ReferenceEquals(writer, Console.Out)
                && !object.ReferenceEquals(writer, Console.Error);

            // Validate minimum level; coerce and surface when invalid
            string[] allowed = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
            if (Array.IndexOf(allowed, _minLevel) < 0)
            {
                var prev = _minLevel;
                _minLevel = "trace";
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "console_minimum_level_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, prev));
                }
                catch { /* swallow */ }
            }

            // Coerce DropOldest -> DropNewest (unobservable drops otherwise)
            if (_dropPolicy == DropPolicy.DropOldest)
            {
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "console_drop_policy_dropoldest"),
                        new KeyValuePair<string, object>(TagKeys.Policy, "coerced_to_dropnewest"));
                }
                catch { /* swallow */ }
                _dropPolicy = DropPolicy.DropNewest;
            }


            // Allow capacity to be set via ctor param or DiagnosticsOptions.DispatcherQueueCapacity (ops‑configurable)
            var capacity = queueCapacity
                ?? (DiagnosticsCoreBootstrap.IsStarted
                        ? DiagnosticsCoreBootstrap.Instance.Options.DispatcherQueueCapacity
                        : 1024);

            if (capacity <= 0)
            {
                capacity = 1024;
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "console_queue_capacity_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, "coerced_to_1024"));
                }
                catch { /* swallow */ }
            }
            var opts = new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                // Never use DropOldest here; it evicts invisibly inside the channel.
                FullMode = _dropPolicy switch
                {
                    DropPolicy.DropNewest => BoundedChannelFullMode.DropWrite,
                    // DropOldest coerced above; keep DropWrite here
                    DropPolicy.BlockWithTimeout => BoundedChannelFullMode.Wait,
                    _ => BoundedChannelFullMode.DropWrite
                }
            };

            _queue = Channel.CreateBounded<string>(opts);
            _worker = Task.Run(WorkerLoop);
            _isActive = 1;

            // Allow drain timeout to be set via ctor param or DiagnosticsOptions if available (ops‑configurable)
            _shutdownDrainTimeoutMs = shutdownDrainTimeoutMs
                ?? (DiagnosticsCoreBootstrap.IsStarted
                        && DiagnosticsCoreBootstrap.Instance.Options is DiagnosticsOptions.ISinkShutdownDrainTimeoutConfig cfg
                        ? cfg.SinkShutdownDrainTimeoutMs
                        : 750);

            // Formatter safeguards (tag truncation)
            _maxTagValueLen = DiagnosticsCoreBootstrap.IsStarted
                ? Math.Max(1, DiagnosticsCoreBootstrap.Instance.Options.MaxTagValueLength)
                : 64;

            // Write latency guard (ms) — protects the pump when Console/redirect is very slow
            _maxWriteLatencyMs = maxWriteLatencyMs is { } v && v > 0 ? v : 2000;

            // Register self-metrics and config gauges together
            if (_metrics is not null)
            {
                TryRegisterSelfMetrics();
                try
                {
                    var capDef = new MetricDefinition(
                        Name: "diagcore.traces.console_sink.queue_capacity",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured bounded queue capacity for ConsoleTraceSink",
                        Unit: "count",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        LongCallback: () => new[] { new System.Diagnostics.Metrics.Measurement<long>(capacity) });
                    var drainDef = new MetricDefinition(
                        Name: "diagcore.traces.console_sink.shutdown_drain_timeout_ms",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured shutdown drain timeout for ConsoleTraceSink",
                        Unit: "ms",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        LongCallback: () => new[] { new System.Diagnostics.Metrics.Measurement<long>(_shutdownDrainTimeoutMs) });
                    var latencyGuardDef = new MetricDefinition(
                        Name: "diagcore.traces.console_sink.max_write_latency_ms",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured write latency guard for ConsoleTraceSink",
                        Unit: "ms",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        LongCallback: () => new[] { new System.Diagnostics.Metrics.Measurement<long>(_maxWriteLatencyMs) });

                    if (_metrics is MetricsFacade mf)
                    {
                        mf.TryRegister(capDef);
                        mf.TryRegister(drainDef);
                        mf.TryRegister(latencyGuardDef);
                    }
                    else
                    {
                        _metrics.Register(capDef);
                        _metrics.Register(drainDef);
                        _metrics.Register(latencyGuardDef);
                    }
                }
                catch { /* swallow */ }
            }

        }

        public void Emit(TraceEvent traceEvent)
        {
            if (traceEvent is null) throw new ArgumentNullException(nameof(traceEvent));

            // Fast-fail if disposed: refuse to enqueue and count a precise drop cause.
            if (Volatile.Read(ref _disposed) != 0)
            {
                TryCountDrop(DropCauses.InvalidOperation);
                return;
            }

            if (!IsLevelAllowed(traceEvent.Status))
            {
                // Surface filtered events for ops parity with TraceManager
                try
                {
                    _metrics?.Increment(
                        DiagCoreMetrics.TracesDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.BelowMinimum),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(ConsoleTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                }
                catch { /* swallow */ }
                return;
            }

            // Preserve original formatting
            var msg = FormatMessage(traceEvent);

            // Backpressure behavior based on policy
            bool wrote = false;
            switch (_dropPolicy)
            {
                case DropPolicy.BlockWithTimeout:
                    {
                        var sw = Stopwatch.StartNew();
                        // First fast path
                        wrote = _queue.Writer.TryWrite(msg);
                        if (!wrote)
                        {
                            // Non-blocking bounded wait loop to avoid sync-over-async deadlocks
                            _metrics?.Increment(
                                DiagCoreMetrics.ConsoleSinkWaits.Name, 1,
                                new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));

                            var spinner = new SpinWait();
                            int spins = 0;
                            while (sw.Elapsed < _blockTimeout && !_cts.IsCancellationRequested)
                            {
                                if (_queue.Writer.TryWrite(msg)) { wrote = true; break; }
                                spinner.SpinOnce();
                                spins++;
                                // Fairness: yield after a few spins to reduce CPU under saturation
                                if (spins > 10) Thread.Sleep(0);
                                if (spins > 50) Thread.Sleep(1);
                            }
                            if (!wrote)
                                _metrics?.Increment(
                                    DiagCoreMetrics.ConsoleSinkWaitTimeouts.Name, 1,
                                    new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                        }
                        break;
                    }

                default:
                    wrote = _queue.Writer.TryWrite(msg);
                    break;
            }

            if (!wrote)
            {
                Interlocked.Increment(ref _droppedCount);
                // Granular overflow cause for ops clarity
                var cause = _dropPolicy == DropPolicy.DropNewest
                    ? DropCauses.OverflowDropNewest
                    : DropCauses.SinkOverflow;
                TryCountDrop(cause);
                return;
            }

            Interlocked.Increment(ref _queueCount);
        }

        private async Task WorkerLoop()
        {
            try
            {
                await foreach (var msg in _queue.Reader.ReadAllAsync(_cts.Token))
                {
                    // Fast-fail if circuit is open
                    if (Volatile.Read(ref _circuitOpenUntilTicks) > DateTime.UtcNow.Ticks)
                    {
                        TryCountDrop(DropCauses.CircuitOpen);
                        Interlocked.Increment(ref _writeErrors);
                        _metrics?.Increment(DiagCoreMetrics.ConsoleSinkWriteErrors.Name, 1);
                        Interlocked.Decrement(ref _queueCount);
                        continue;
                    }
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        // Retry transient console write errors up to 3 times with small backoff
                        int attempts = 0;
                        while (true)
                        {
                            try
                            {
                                _writer.WriteLine(msg);
                                break;
                            }
                            catch (Exception ex) when (
                                (ex is IOException
                                 || ex is ObjectDisposedException
                                 || ex is InvalidOperationException)
                                && attempts++ < 3)
                            {
                                await Task.Delay(10 * attempts, _cts.Token).ConfigureAwait(false);
                            }
                        }
                        // Always mirror to Trace listeners (non-blocking API)
                        try { Trace.WriteLine(msg); } catch { /* swallow trace listener faults */ }
                        Interlocked.Increment(ref _messagesWritten);
                        var stop = Stopwatch.GetTimestamp();
                        var latency = (double)(stop - start) * 1000 / Stopwatch.Frequency;
                        System.Threading.Volatile.Write(ref _lastWriteLatencyMs, latency);
                        _metrics?.Increment(DiagCoreMetrics.ConsoleSinkMessagesWritten.Name, 1);
                        _metrics?.Record(DiagCoreMetrics.ConsoleSinkWriteLatencyMs.Name, _lastWriteLatencyMs);
                        // Reset breaker on success
                        _consecutiveWriteErrors = 0;


                        // Write latency guard accounting — surface as a soft drop signal for ops
                        if (_lastWriteLatencyMs > _maxWriteLatencyMs)
                            _metrics?.Increment(DiagCoreMetrics.ConsoleSinkWriteLatencyExceeded.Name, 1);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ConsoleTraceSink] Write failed: {ex}");
                        Interlocked.Increment(ref _writeErrors);
                        _metrics?.Increment(
                            DiagCoreMetrics.ConsoleSinkWriteErrors.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Exception, ex.GetType().Name));
                        TryCountDrop(DropCauses.SinkError);
                        // Open circuit with exponential backoff (bounded 1s..32s, capped at 30s)
                        var cur = Math.Min(30000, 1000 * (1 << Math.Min(5, _consecutiveWriteErrors++)));
                        Volatile.Write(ref _circuitOpenUntilTicks, DateTime.UtcNow.AddMilliseconds(cur).Ticks);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _queueCount);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                // Prevent silent task fault; surface a soft signal and mark inactive
                Debug.WriteLine($"[ConsoleTraceSink] Worker faulted: {ex}");
                TryCountDrop(DropCauses.PumpFault);
            }
            finally
            {
                Interlocked.Exchange(ref _isActive, 0);
            }
        }

        private bool IsLevelAllowed(string status)
        {
            string[] order = Silica.DiagnosticsCore.Internal.AllowedLevels.TraceAndLogLevels;
            var s = (status ?? "").Trim().ToLowerInvariant();
            int idxStatus = Array.IndexOf(order, s);
            int idxMin = Array.IndexOf(order, _minLevel);
            if (idxStatus < 0) idxStatus = Array.IndexOf(order, "info");
            return idxStatus >= idxMin;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
            // Complete and drain briefly
            _queue.Writer.TryComplete();
            try { _worker.Wait(_shutdownDrainTimeoutMs); } catch { /* best-effort */ }

            // Ensure cancel in case of slow/blocked writer
            try { _cts.Cancel(); } catch { /* swallow */ }
            try { _worker.Wait(50); } catch { /* ignore */ }
            _cts.Dispose();
            _isActive = 0;
            if (_ownsWriter)
            {
                try { _writer.Dispose(); } catch { /* swallow */ }
            }

            // Surface backlog count on disposal
            var backlog = QueueDepth;
            if (backlog > 0)
            {
                try
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, backlog,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.ShutdownBacklog),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(ConsoleTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                }
                catch { /* swallow */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
            _queue.Writer.TryComplete();
            try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(_shutdownDrainTimeoutMs)).ConfigureAwait(false); } catch { /* best-effort */ }
            try { _cts.Cancel(); } catch { /* swallow */ }
            try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
            _isActive = 0;
            if (_ownsWriter)
            {
                try { _writer.Dispose(); } catch { /* swallow */ }
            }


            var backlog = QueueDepth;
            if (backlog > 0)
            {
                try
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, backlog,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.ShutdownBacklog),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(ConsoleTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                }
                catch { /* swallow */ }
            }
        }

        // ---- Self-metrics surface (poll from your registry) ----
        internal int QueueDepth => Volatile.Read(ref _queueCount);
        internal long DroppedCount => Interlocked.Read(ref _droppedCount);
        internal long MessagesWritten => Interlocked.Read(ref _messagesWritten);
        internal long WriteErrors => Interlocked.Read(ref _writeErrors);
        internal int IsActive => Volatile.Read(ref _isActive);
        internal double LastWriteLatencyMs => System.Threading.Volatile.Read(ref _lastWriteLatencyMs);

        private void TryRegisterSelfMetrics()
        {
            if (_metrics is null) return;
            try
            {
                if (_metrics is MetricsFacade mf)
                {
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkMessagesWritten);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkWriteErrors);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkDrops);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkWriteLatencyMs);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkWaits);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkWaitTimeouts);
                    mf.TryRegister(DiagCoreMetrics.TracesDropped);
                    mf.TryRegister(DiagCoreMetrics.ConsoleSinkWriteLatencyExceeded);
                }
                else
                {
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkMessagesWritten);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkWriteErrors);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkDrops);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkWriteLatencyMs);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkWaits);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkWaitTimeouts);
                    _metrics.Register(DiagCoreMetrics.TracesDropped);
                    _metrics.Register(DiagCoreMetrics.ConsoleSinkWriteLatencyExceeded);
                }


                void Reg(MetricDefinition def,
                         Func<IEnumerable<System.Diagnostics.Metrics.Measurement<long>>>? longCb = null,
                         Func<IEnumerable<System.Diagnostics.Metrics.Measurement<double>>>? dblCb = null)
                {
                    var d = def with { LongCallback = longCb, DoubleCallback = dblCb };
                    if (_metrics is MetricsFacade mf) mf.TryRegister(d);
                    else _metrics.Register(d);
                }

                Reg(DiagCoreMetrics.ConsoleSinkQueueDepth, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(QueueDepth)
                });

                Reg(DiagCoreMetrics.ConsoleSinkDroppedTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(DroppedCount)
                });

                Reg(DiagCoreMetrics.ConsoleSinkMessagesWrittenTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(MessagesWritten)
                });

                Reg(DiagCoreMetrics.ConsoleSinkWriteErrorsTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(WriteErrors)
                });

                Reg(DiagCoreMetrics.ConsoleSinkActive, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(IsActive)
                });

                Reg(DiagCoreMetrics.ConsoleSinkLastWriteLatencyMs, null, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<double>(LastWriteLatencyMs)
                });

            }
            catch
            {
                // never fail sink init on exporter issues
            }
        }

        private void TryCountDrop(string cause)
        {
            try
            {
                _metrics?.Increment(
                    DiagCoreMetrics.TracesDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, cause),
                    new KeyValuePair<string, object>(TagKeys.Sink, nameof(ConsoleTraceSink)),
                    new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                _metrics?.Increment(DiagCoreMetrics.ConsoleSinkDrops.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
            }
            catch
            {
                // swallow
            }
        }

        private string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= _maxTagValueLen ? s : s.Substring(0, _maxTagValueLen);
        }

        private string FormatMessage(TraceEvent e)
        {
            var sb = new StringBuilder(256);
            sb.Append(e.Timestamp.ToString("O"))
              .Append(" [").Append(e.Status).Append("] ")
              .Append(e.Component).Append('/')
              .Append(e.Operation).Append(' ')
              .Append(e.Message);
              //.Append(" cid=").Append(e.CorrelationId.ToString("D"))
              //.Append(" sid=").Append(e.SpanId.ToString("D"));

            if (_fmtIncludeTags && e.Tags != null && e.Tags.Count > 0)
            {
                sb.Append(" tags=");
                bool first = true;
                foreach (var kv in e.Tags)
                {
                    if (!first) sb.Append(',');
                    sb.Append(kv.Key).Append('=').Append(Trunc(kv.Value));
                    first = false;
                }
            }

            if (_fmtIncludeException && e.Exception != null)
            {
                sb.Append(" ex=").Append(e.Exception.GetType().Name)
                  .Append(": ").Append(e.Exception.Message);
            }

            return sb.ToString();
        }
    }
}
