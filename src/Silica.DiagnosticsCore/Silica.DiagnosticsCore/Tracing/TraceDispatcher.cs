// File: Silica.DiagnosticsCore/Tracing/TraceDispatcher.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Diagnostics.Metrics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Thread‑safe dispatcher for broadcasting <see cref="TraceEvent"/>s to multiple sinks.
    /// </summary>
    /// <remarks>
    /// - Registering sinks is idempotent per instance (no duplicates).
    /// - Dispatch is lock‑free on the hot path; sinks snapshot is immutable.
    /// - Per‑sink exceptions are swallowed to avoid blocking or halting the dispatch loop.
    /// </remarks>
    public sealed class TraceDispatcher : IDisposable
    {
        private ITraceSink[] _sinks = Array.Empty<ITraceSink>();
        private readonly object _registerGate = new();
        private readonly ConcurrentDictionary<ITraceSink, Channel<TraceEvent>> _queues = new();
        private readonly ConcurrentDictionary<ITraceSink, Task> _pumps = new();
        private readonly ConcurrentDictionary<ITraceSink, string> _sinkPolicies = new();
        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;
        private readonly int _queueCapacity;
        private readonly int _shutdownTimeoutMs;
        private readonly CancellationTokenSource _cts = new();

        public TraceDispatcher(
        Silica.DiagnosticsCore.Metrics.IMetricsManager? metrics = null,
        int queueCapacity = 1024,
        int shutdownTimeoutMs = 5000,
        bool registerGauges = true)
        {
            _metrics = metrics;
            _queueCapacity = queueCapacity;
            _shutdownTimeoutMs = Math.Max(0, shutdownTimeoutMs);


            if (_metrics is not null && registerGauges)
            {
                void RegisterOnce(Silica.DiagnosticsCore.Metrics.MetricDefinition def)
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(def);
                    else _metrics.Register(def);
                }

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.dispatcher.sinks",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Number of registered trace sinks",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () => new[] { new Measurement<long>(_queues.Count) }));

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.dispatcher.pumps_active",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Active pump tasks for registered sinks",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () => new[] { new Measurement<long>(_pumps.Count) }));

                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.dispatcher.queue_depth",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Total pending items across all sink queues",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () =>
                    {
                        long depth = 0;
                        foreach (var kv in _queues)
                            depth += kv.Value.Reader.Count;
                        return new[] { new Measurement<long>(depth) };
                    }));

                RegisterOnce(DiagCoreMetrics.SinkEmitDurationMs);
                RegisterOnce(DiagCoreMetrics.SinkEmitLong);
                RegisterOnce(DiagCoreMetrics.DispatcherShutdownQueueDepth);
            }
        }

        public void RegisterSink(ITraceSink sink, BoundedChannelFullMode fullMode = BoundedChannelFullMode.DropWrite)
        {
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));

            // Serialize registration to avoid CAS contention failures
            lock (_registerGate)
            {
                var snapshot = _sinks;
                if (Array.Exists(snapshot, s => ReferenceEquals(s, sink)))
                    return; // already registered

                // Create channel for this sink
                var channel = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(_queueCapacity)
                {
                    SingleWriter = false,
                    SingleReader = true,
                    FullMode = fullMode
                });

                _queues[sink] = channel;
                _sinkPolicies[sink] = fullMode.ToString().ToLowerInvariant();

                // Build new sink array and publish
                var updated = new ITraceSink[snapshot.Length + 1];
                Array.Copy(snapshot, updated, snapshot.Length);
                updated[^1] = sink;
                Volatile.Write(ref _sinks, updated);

                _pumps[sink] = Task.Run(() => PumpAsync(sink, channel, _cts.Token));
            }
        }

        public void Dispatch(TraceEvent evt)
        {
            if (evt is null) throw new ArgumentNullException(nameof(evt));
            var snapshot = _sinks;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (_queues.TryGetValue(snapshot[i], out var ch))
                {
                    if (!ch.Writer.TryWrite(evt))
                    {
                        var policy = _sinkPolicies.TryGetValue(snapshot[i], out var p) ? p : "unknown";
                        var sinkType = snapshot[i].GetType().Name;
                        _metrics?.Increment(
                            Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                            1,
                            new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, "queue_full"),
                            new KeyValuePair<string, object>(TagKeys.Policy, policy),
                            new KeyValuePair<string, object>(TagKeys.Sink, sinkType));
                    }
                }
                else
                {
                    // Legacy sink path: enforce accounting or emit warning
                    var sinkType = snapshot[i].GetType().Name;
                    _metrics?.Increment(
                        Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                        1,
                        new KeyValuePair<string, object>(
                            Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, "unregistered_sink"),
                        new KeyValuePair<string, object>(TagKeys.Sink, sinkType));
                    // Optionally: direct emit if you still want to support legacy sinks
                    // try { snapshot[i].Emit(evt); } catch { /* swallow */ }
                }
            }
        }

        private async Task PumpAsync(ITraceSink sink, Channel<TraceEvent> channel, CancellationToken ct)
        {
            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        sink.Emit(evt);
                        sw.Stop();
                        try
                        {
                            _metrics?.Record(
                                DiagCoreMetrics.SinkEmitDurationMs.Name,
                                sw.Elapsed.TotalMilliseconds,
                                new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name));
                            if (sw.ElapsedMilliseconds > 1000)
                                _metrics?.Increment(
                                    DiagCoreMetrics.SinkEmitLong.Name,                                    1,
                                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name));
                        }
                        catch { /* swallow exporter/disposal errors */ }                    }
                    catch
                    {
                        try
                        {
                            _metrics?.Increment(
                            Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                            1,
                            new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, "sink_error"),
                            new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name)); 
                        }
                        catch { /* swallow */ }                    
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown via cancellation; do not surface as a fault.
            }
            catch
            {
                // Pump fault: record once on termination to surface operator signal
                try
                {
                    _metrics?.Increment(
                     Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                    1,
                    new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, "pump_fault"),
                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name)); 
                }
                catch { /* swallow */ }            
            }
        }

        public void Dispose()
        {
           var sw = System.Diagnostics.Stopwatch.StartNew();
           // Signal shutdown and complete writers to stop pumps
           _cts.Cancel();
           foreach (var ch in _queues.Values)
               ch.Writer.TryComplete();

           // Best-effort wait for pumps to drain
           try { Task.WhenAll(_pumps.Values).Wait(TimeSpan.FromMilliseconds(_shutdownTimeoutMs)); }
           catch { /* accounted */ }
           finally
           {
               if (sw.ElapsedMilliseconds > _shutdownTimeoutMs && _metrics is not null)
               {
                   try
                   {
                       _metrics.Increment(
                           DiagCoreMetrics.TracesDropped.Name, 1,
                           new KeyValuePair<string, object>(TagKeys.DropCause, "dispatcher_shutdown_timeout"));
                       long depth = 0;
                       foreach (var kv in _queues)
                           depth += kv.Value.Reader.Count;
                        _metrics.Record(
                        DiagCoreMetrics.DispatcherShutdownQueueDepth.Name, depth);

                       // Surface top sinks with backlog for operator visibility
                       foreach (var kv in _queues)
                       {
                           var remaining = kv.Value.Reader.Count;
                           if (remaining > 0)
                           {
                               try
                               {
                                   _metrics.Increment(
                                       DiagCoreMetrics.TracesDropped.Name, remaining,
                                       new KeyValuePair<string, object>(TagKeys.DropCause, "dispatcher_shutdown_backlog"),
                                       new KeyValuePair<string, object>(TagKeys.Sink, kv.Key.GetType().Name));
                               }
                               catch { /* swallow */ }
                           }
                       }
                    }
                   catch { /* swallow exporter/disposal errors */ }
                }
               _cts.Dispose();
           }
           _queues.Clear();
           _pumps.Clear();
           _sinkPolicies.Clear();
           _sinks = Array.Empty<ITraceSink>();
        }
    }
}
