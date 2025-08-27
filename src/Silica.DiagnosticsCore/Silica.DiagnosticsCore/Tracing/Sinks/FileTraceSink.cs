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
    internal sealed class FileTraceSink : ITraceSink, IDisposable, IAsyncDisposable
    {
        private int _disposed; // 0 = not disposed, 1 = disposed
        private readonly string _minLevel;
        private readonly IMetricsManager? _metrics;
        private StreamWriter _writer;
        private readonly string _filePath;
        private readonly bool _durableWrites;

        private readonly Channel<string> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        private long _droppedCount;
        private int _queueCount;
        private long _messagesWritten;
        private long _writeErrors;
        private volatile int _isActive;
        private double _lastWriteLatencyMs;
        private readonly int _shutdownDrainTimeoutMs;
        private readonly int _maxTagValueLen;
        private readonly int _maxWriteLatencyMs;
        // Rotation/retention (optional)
        private readonly long? _maxBytesPerFile;
        private readonly int? _maxFiles;
        // Circuit-breaker / backoff for persistent errors
        private int _consecutiveWriteErrors;
        private long _circuitOpenUntilTicks;


        internal enum DropPolicy
        {
            DropNewest = 0,
            DropOldest = 1,
            BlockWithTimeout = 2
        }

        private readonly DropPolicy _dropPolicy;
        private readonly TimeSpan _blockTimeout;

        private bool _fmtIncludeTags;
        private bool _fmtIncludeException;

        public FileTraceSink WithFormatting(bool includeTags, bool includeException)
        {
            _fmtIncludeTags = includeTags;
            _fmtIncludeException = includeException;
            return this;
        }

        public FileTraceSink(
            string filePath,
            string minLevel,
            IMetricsManager? metrics = null,
            DropPolicy dropPolicy = DropPolicy.DropNewest,
            TimeSpan? blockTimeout = null,
            int? queueCapacity = null,
            int? shutdownDrainTimeoutMs = null,
            int? maxWriteLatencyMs = null,
            Encoding? encoding = null,
            bool emitBom = false,
            long? maxBytesPerFile = null,
            int? maxFiles = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            _filePath = filePath;
            _minLevel = (minLevel ?? "trace").Trim().ToLowerInvariant();
            _metrics = metrics;
            _dropPolicy = dropPolicy;
            _blockTimeout = blockTimeout ?? TimeSpan.FromMilliseconds(10);
            _maxBytesPerFile = maxBytesPerFile;
            _maxFiles = maxFiles;

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
                        new KeyValuePair<string, object>(TagKeys.Field, "file_minimum_level_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, prev));
                }
                catch { /* swallow */ }
            }

            // Ensure directory exists before opening
            try
            {
                var fiEnsure = new FileInfo(filePath);
                if (fiEnsure.Directory != null) fiEnsure.Directory.Create();
            }
            catch
            {
                // swallow; writer open below may still fail and be retried in worker path
            }

            // Open file in append mode, UTF-8, buffered — be resilient to initial IO failures.
            var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom);
            try
            {
                _writer = new StreamWriter(
                    new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    enc)
                { AutoFlush = true };
            }
            catch (Exception ex)
            {
                // Fall back to a null writer and open a short circuit; the worker loop will continue
                // to count errors and can attempt recovery (e.g., after directory re-creation).
                _writer = new StreamWriter(Stream.Null, enc) { AutoFlush = true };
                try
                {
                    // Surface the initialization failure for operators.
                    _metrics?.Increment(
                        DiagCoreMetrics.FileSinkWriteErrors.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Exception, ex.GetType().Name));
                }
                catch { /* swallow */ }
                // Short backoff to protect the pump on immediate startup failure
                Volatile.Write(ref _circuitOpenUntilTicks, DateTime.UtcNow.AddSeconds(5).Ticks);
            }

            _durableWrites = Environment.GetEnvironmentVariable("FILETRACE_DURABLE_WRITES") == "1";
            // Coerce DropOldest -> DropNewest (unobservable channel evictions)
            if (_dropPolicy == DropPolicy.DropOldest)
            {
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "file_drop_policy_dropoldest"),
                        new KeyValuePair<string, object>(TagKeys.Policy, "coerced_to_dropnewest"));
                }
                catch { /* swallow */ }
                _dropPolicy = DropPolicy.DropNewest;
            }



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
                        new KeyValuePair<string, object>(TagKeys.Field, "file_sink_queue_capacity_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, "coerced_to_1024"));
                }
                catch { /* swallow */ }
            }

            var opts = new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                // Never allow DropOldest here; we want observable drops.
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

            _shutdownDrainTimeoutMs = shutdownDrainTimeoutMs
                ?? (DiagnosticsCoreBootstrap.IsStarted
                        && DiagnosticsCoreBootstrap.Instance.Options is DiagnosticsOptions.ISinkShutdownDrainTimeoutConfig cfg
                        ? cfg.SinkShutdownDrainTimeoutMs
                        : 750);

            _maxTagValueLen = DiagnosticsCoreBootstrap.IsStarted
                ? Math.Max(1, DiagnosticsCoreBootstrap.Instance.Options.MaxTagValueLength)
                : 64;

            _maxWriteLatencyMs = maxWriteLatencyMs is { } v && v > 0 ? v : 2000;

            if (_metrics is not null)
            {
                TryRegisterSelfMetrics();
                try
                {
                    // Config gauges (mirror ConsoleTraceSink behavior)
                    var capDef = new MetricDefinition(
                        Name: "diagcore.traces.file_sink.queue_capacity",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured bounded queue capacity for FileTraceSink",
                        Unit: "count",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        LongCallback: () => new[] { new System.Diagnostics.Metrics.Measurement<long>(capacity) });
                    var drainDef = new MetricDefinition(
                        Name: "diagcore.traces.file_sink.shutdown_drain_timeout_ms",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured shutdown drain timeout for FileTraceSink",
                        Unit: "ms",
                        DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                        LongCallback: () => new[] { new System.Diagnostics.Metrics.Measurement<long>(_shutdownDrainTimeoutMs) });
                    var latencyGuardDef = new MetricDefinition(
                        Name: "diagcore.traces.file_sink.max_write_latency_ms",
                        Type: MetricType.ObservableGauge,
                        Description: "Configured write latency guard for FileTraceSink",
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
                catch { /* swallow exporter issues */ }
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
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(FileTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                }
                catch { /* swallow */ }
                return;
            }

            var msg = FormatMessage(traceEvent);
            bool wrote = false;

            switch (_dropPolicy)
            {
                case DropPolicy.BlockWithTimeout:
                    var sw = Stopwatch.StartNew();
                    wrote = _queue.Writer.TryWrite(msg);
                    if (!wrote)
                    {
                        _metrics?.Increment(
                            DiagCoreMetrics.FileSinkWaits.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));

                        var spinner = new SpinWait();
                        int spins = 0;
                        while (sw.Elapsed < _blockTimeout && !_cts.IsCancellationRequested)
                        {
                            if (_queue.Writer.TryWrite(msg)) { wrote = true; break; }
                            spinner.SpinOnce();
                            spins++;
                            if (spins > 10) Thread.Sleep(0);
                            if (spins > 50) Thread.Sleep(1);
                        }
                        if (!wrote)
                            _metrics?.Increment(
                                DiagCoreMetrics.FileSinkWaitTimeouts.Name, 1,
                                new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                    }
                    break;
                default:
                    wrote = _queue.Writer.TryWrite(msg);
                    break;
            }

            if (!wrote)
            {
                Interlocked.Increment(ref _droppedCount);
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
                        _metrics?.Increment(DiagCoreMetrics.FileSinkWriteErrors.Name, 1);
                        Interlocked.Decrement(ref _queueCount);
                        continue;
                    }


                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        int attempts = 0;
                        while (true)
                        {
                            // Optional: rotate file if size/time policy triggers
                            TryRotateIfNeeded();
                            try
                            {
                                _writer.WriteLine(msg);
                                if (_durableWrites)
                                {
                                    try
                                    {
                                        _writer.Flush();
                                        if (_writer.BaseStream is FileStream fs) fs.Flush(true);
                                    }
                                    catch { /* swallow fsync errors */ }
                                }
                                break;
                            }
                            catch (IOException) when (attempts++ < 3)
                            {
                                await Task.Delay(10 * attempts, _cts.Token).ConfigureAwait(false);
                            }
                            catch (DirectoryNotFoundException) when (attempts++ < 1)
                            {
                                try
                                {
                                    var fi = new FileInfo(_filePath);
                                    fi.Directory?.Create();
                                    _writer.Dispose();
                                    _writer = new StreamWriter(
                                        new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                                    { AutoFlush = true };
                                }
                                catch { /* swallow reopen errors */ }
                            }
                        }

                        Interlocked.Increment(ref _messagesWritten);
                        var stop = Stopwatch.GetTimestamp();
                        var latency = (double)(stop - start) * 1000 / Stopwatch.Frequency;
                        System.Threading.Volatile.Write(ref _lastWriteLatencyMs, latency);
                        _metrics?.Increment(DiagCoreMetrics.FileSinkMessagesWritten.Name, 1);
                        _metrics?.Record(DiagCoreMetrics.FileSinkWriteLatencyMs.Name, _lastWriteLatencyMs);
                        // Reset breaker on success
                        _consecutiveWriteErrors = 0;


                        if (_lastWriteLatencyMs > _maxWriteLatencyMs)
                            _metrics?.Increment(DiagCoreMetrics.FileSinkWriteLatencyExceeded.Name, 1);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _writeErrors);
                        _metrics?.Increment(
                            DiagCoreMetrics.FileSinkWriteErrors.Name,
                            1,
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Prevent silent task fault; surface a soft signal and mark inactive (parity with ConsoleTraceSink)
                Debug.WriteLine("[FileTraceSink] Worker faulted: " + ex);
                TryCountDrop(DropCauses.PumpFault);
            }
            finally
            {
                Interlocked.Exchange(ref _isActive, 0);
            }
        }
        private void TryRotateIfNeeded()
        {
            if (_maxBytesPerFile is null && _maxFiles is null) return;
            try
            {
                if (!(_writer.BaseStream is FileStream fs)) return;
                if (_maxBytesPerFile is not null && fs.Length >= _maxBytesPerFile.Value)
                {
                    _writer.Flush();
                    fs.Flush(true);
                    _writer.Dispose();

                    var now = DateTime.UtcNow;
                    var dir = Path.GetDirectoryName(_filePath) ?? ".";
                    var name = Path.GetFileNameWithoutExtension(_filePath);
                    var ext = Path.GetExtension(_filePath);
                    var baseRolled = Path.Combine(dir, name + "." + now.ToString("yyyyMMdd_HHmmss") + "." + ext.TrimStart('.'));
                    string rolled = baseRolled;
                    int attempt = 0;
                    // Resolve collisions deterministically without LINQ
                    while (File.Exists(rolled) && attempt < 100)
                    {
                        attempt++;
                        rolled = baseRolled + "." + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    // If still exists after attempts, fall back to last probed name; File.Move may still throw, we swallow below.
                    try
                    {
                        File.Move(_filePath, rolled, overwrite: false);
                    }
                    catch
                    {
                        // Swallow rotation move errors; continue with a fresh writer on _filePath
                    }

                    _writer = new StreamWriter(
                        new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    { AutoFlush = true };

                    // Retention: keep newest N rolled files
                    if (_maxFiles is { } max && max > 0)
                    {
                        string[] files = Directory.GetFiles(dir, name + ".*." + ext.TrimStart('.'));
                        // Sort newest first by creation time
                        Array.Sort(files, delegate (string a, string b)
                        {
                            DateTime ca = File.GetCreationTimeUtc(a);
                            DateTime cb = File.GetCreationTimeUtc(b);
                            return cb.CompareTo(ca);
                        });
                        for (int i = max; i < files.Length; i++)
                        {
                            try { File.Delete(files[i]); } catch { /* swallow retention errors */ }
                        }
                    }
                }
            }
            catch
            {
                // Rotation should never throw on hot path
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return; // already disposed

            _queue.Writer.TryComplete();
            try { _worker.Wait(_shutdownDrainTimeoutMs); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _worker.Wait(50); } catch { }
            _cts.Dispose();
            _isActive = 0;
            _writer.Dispose();
            // Surface backlog count on disposal (parity with ConsoleTraceSink)
            var backlog = QueueDepth;
            if (backlog > 0)
            {
                try
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, backlog,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.ShutdownBacklog),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(FileTraceSink)),
                        new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                }
                catch { /* swallow */ }
            }

        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return; // already disposed

            _queue.Writer.TryComplete();
            try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(_shutdownDrainTimeoutMs)).ConfigureAwait(false); } catch { /* best-effort */ }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
            _isActive = 0;
            _writer.Dispose();
            var backlog = QueueDepth;
            if (backlog > 0)
            {
                try
                {
                    _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, backlog,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.ShutdownBacklog),
                        new KeyValuePair<string, object>(TagKeys.Sink, nameof(FileTraceSink)),
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
                    mf.TryRegister(DiagCoreMetrics.FileSinkMessagesWritten);
                    mf.TryRegister(DiagCoreMetrics.FileSinkWriteErrors);
                    mf.TryRegister(DiagCoreMetrics.FileSinkDrops);
                    mf.TryRegister(DiagCoreMetrics.FileSinkWriteLatencyMs);
                    mf.TryRegister(DiagCoreMetrics.FileSinkWaits);
                    mf.TryRegister(DiagCoreMetrics.FileSinkWaitTimeouts);
                    mf.TryRegister(DiagCoreMetrics.TracesDropped);
                    mf.TryRegister(DiagCoreMetrics.FileSinkWriteLatencyExceeded);
                }
                else
                {
                    _metrics.Register(DiagCoreMetrics.FileSinkMessagesWritten);
                    _metrics.Register(DiagCoreMetrics.FileSinkWriteErrors);
                    _metrics.Register(DiagCoreMetrics.FileSinkDrops);
                    _metrics.Register(DiagCoreMetrics.FileSinkWriteLatencyMs);
                    _metrics.Register(DiagCoreMetrics.FileSinkWaits);
                    _metrics.Register(DiagCoreMetrics.FileSinkWaitTimeouts);
                    _metrics.Register(DiagCoreMetrics.TracesDropped);
                    _metrics.Register(DiagCoreMetrics.FileSinkWriteLatencyExceeded);
                }

                void Reg(MetricDefinition def,
                         Func<IEnumerable<System.Diagnostics.Metrics.Measurement<long>>>? longCb = null,
                         Func<IEnumerable<System.Diagnostics.Metrics.Measurement<double>>>? dblCb = null)
                {
                    var d = def with { LongCallback = longCb, DoubleCallback = dblCb };
                    if (_metrics is MetricsFacade mf) mf.TryRegister(d);
                    else _metrics.Register(d);
                }

                Reg(DiagCoreMetrics.FileSinkQueueDepth, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(QueueDepth)
                });

                Reg(DiagCoreMetrics.FileSinkDroppedTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(DroppedCount)
                });

                Reg(DiagCoreMetrics.FileSinkMessagesWrittenTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(MessagesWritten)
                });

                Reg(DiagCoreMetrics.FileSinkWriteErrorsTotal, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(WriteErrors)
                });

                Reg(DiagCoreMetrics.FileSinkActive, () => new[]
                {
                    new System.Diagnostics.Metrics.Measurement<long>(IsActive)
                });

                Reg(DiagCoreMetrics.FileSinkLastWriteLatencyMs, null, () => new[]
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
                    new KeyValuePair<string, object>(TagKeys.Sink, nameof(FileTraceSink)),
                    new KeyValuePair<string, object>(TagKeys.Policy, _dropPolicy.ToString().ToLowerInvariant()));
                _metrics?.Increment(DiagCoreMetrics.FileSinkDrops.Name, 1,
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
              .Append(e.Message)
              .Append(" cid=").Append(e.CorrelationId.ToString("D"))
              .Append(" sid=").Append(e.SpanId.ToString("D"));

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
