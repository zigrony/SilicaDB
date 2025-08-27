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

        private sealed class SinkState
        {
            public required Channel<TraceEvent> Channel;
            public required string Policy;
            public long Depth; // atomic via Interlocked
            public volatile bool Draining; // suppress fault noise during controlled drains
            public required CancellationTokenSource Cts; // per-sink cancellation (linked to dispatcher)
        }
        private readonly ConcurrentDictionary<ITraceSink, SinkState> _states = new();


        private readonly Silica.DiagnosticsCore.Metrics.IMetricsManager? _metrics;
        private readonly int _queueCapacity;
        private readonly int _shutdownTimeoutMs;
        private volatile bool _disposed;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _enforceRedaction;

        public sealed record HealthSnapshot(
            int RegisteredSinks,
            int ActivePumps,
            long TotalQueueDepth,
            IReadOnlyDictionary<string, long> QueueDepthBySinkType,
            bool IsCancelling);

        public HealthSnapshot GetHealthSnapshot()
        {
            var byType = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var kv in _states)
            {
                var sinkType = kv.Key.GetType().Name;
                var depth = Interlocked.Read(ref kv.Value.Depth);
                total += depth;
                if (byType.TryGetValue(sinkType, out var existing)) byType[sinkType] = existing + depth;
                else byType[sinkType] = depth;
            }

            // Count only pumps that are still active (not completed or faulted).
            int active = 0;
            foreach (var pump in _pumps)
            {
                var task = pump.Value;
                if (task is null) continue;
                if (!task.IsCompleted) active++;
            }

            return new HealthSnapshot(
                RegisteredSinks: Volatile.Read(ref _sinks).Length,
                ActivePumps: active,
                TotalQueueDepth: total,
                QueueDepthBySinkType: byType,
                IsCancelling: _cts.IsCancellationRequested);
        }

        // Ensure we register counters/histograms used by dispatcher even when registerGauges=false
        private void EnsureCoreMetricsRegistered()
        {
            if (_metrics is null) return;
            try
            {
                if (_metrics is MetricsFacade mf)
                {
                    mf.TryRegister(DiagCoreMetrics.TracesDropped);
                    mf.TryRegister(DiagCoreMetrics.DispatcherSinkRegistered);
                    mf.TryRegister(DiagCoreMetrics.DispatcherSinkUnregistered);
                    mf.TryRegister(DiagCoreMetrics.SinkEmitDurationMs);
                    mf.TryRegister(DiagCoreMetrics.SinkEmitLong);
                    mf.TryRegister(DiagCoreMetrics.DispatcherShutdownQueueDepth);
                    mf.TryRegister(DiagCoreMetrics.PumpsRemainingOnTimeout);
                }
                else
                {
                    _metrics.Register(DiagCoreMetrics.TracesDropped);
                    _metrics.Register(DiagCoreMetrics.DispatcherSinkRegistered);
                    _metrics.Register(DiagCoreMetrics.DispatcherSinkUnregistered);
                    _metrics.Register(DiagCoreMetrics.SinkEmitDurationMs);
                    _metrics.Register(DiagCoreMetrics.SinkEmitLong);
                    _metrics.Register(DiagCoreMetrics.DispatcherShutdownQueueDepth);
                    _metrics.Register(DiagCoreMetrics.PumpsRemainingOnTimeout);
                }
            }
            catch { /* swallow */ }
        }

        public TraceDispatcher(
        Silica.DiagnosticsCore.Metrics.IMetricsManager? metrics = null,
        int queueCapacity = 1024,
        int shutdownTimeoutMs = 5000,
        bool registerGauges = true,
        bool enforceRedaction = false)
        {
            _metrics = metrics;
            _enforceRedaction = enforceRedaction;

            EnsureCoreMetricsRegistered();

            // Validate queue capacity
            var qc = queueCapacity;
            if (qc <= 0)
            {
                qc = 1024;
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "dispatcher_queue_capacity_invalid"),
                        new KeyValuePair<string, object>(TagKeys.Policy, queueCapacity.ToString()));
                }
                catch { /* swallow */ }
            }
            _queueCapacity = qc;

            // Clamp and surface negative shutdown timeouts
            if (shutdownTimeoutMs < 0)
            {
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                    _metrics?.Increment(
                        DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Field, "dispatcher_shutdown_timeout_negative"),
                        new KeyValuePair<string, object>(TagKeys.Policy, shutdownTimeoutMs.ToString()));
                }
                catch { /* swallow */ }
            }
            _shutdownTimeoutMs = Math.Max(0, shutdownTimeoutMs);


            if (_metrics is not null && registerGauges)
            {
                void RegisterOnce(Silica.DiagnosticsCore.Metrics.MetricDefinition def)
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(def);
                    else _metrics.Register(def);
                }

                // Expose configured queue capacity for operator correlation with QueueFull drops
                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.dispatcher.queue_capacity",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Configured bounded queue capacity per sink (global)",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () => new[] { new Measurement<long>(_queueCapacity) }));

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

                // Aggregate depths by sink type, not by instance, to avoid high-cardinality series
                RegisterOnce(new Silica.DiagnosticsCore.Metrics.MetricDefinition(
                    Name: "diagcore.traces.dispatcher.queue_depth_by_sink",
                    Type: Silica.DiagnosticsCore.Metrics.MetricType.ObservableGauge,
                    Description: "Approximate pending or in-flight items aggregated by sink type (enqueue/dequeue deltas)",
                    Unit: "count",
                    DefaultTags: Array.Empty<KeyValuePair<string, object>>(),
                    LongCallback: () =>
                    {
                        // Aggregate depths by sink type to avoid duplicate-tag series
                        var perType = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in _states)
                        {
                            var sinkType = kv.Key.GetType().Name;
                            var depth = Interlocked.Read(ref kv.Value.Depth);
                            if (perType.TryGetValue(sinkType, out var existing))
                                perType[sinkType] = existing + depth;
                            else
                                perType[sinkType] = depth;
                        }
                        var list = new List<Measurement<long>>(perType.Count);
                        foreach (var pair in perType)
                        {
                            list.Add(new Measurement<long>(
                                pair.Value,
                                new KeyValuePair<string, object>(TagKeys.Sink, pair.Key)
                            ));
                        }
                        return list;
                    }));
                RegisterOnce(DiagCoreMetrics.SinkEmitDurationMs);
                RegisterOnce(DiagCoreMetrics.SinkEmitLong);
                RegisterOnce(DiagCoreMetrics.DispatcherShutdownQueueDepth);
                RegisterOnce(DiagCoreMetrics.PumpsRemainingOnTimeout);
            }
        }

        public void RegisterSink(ITraceSink sink, BoundedChannelFullMode fullMode = BoundedChannelFullMode.DropWrite)
        {
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            if (_disposed)
                throw new ObjectDisposedException(nameof(TraceDispatcher));

            if (_cts.IsCancellationRequested)
                throw new ObjectDisposedException(nameof(TraceDispatcher), "Dispatcher is shutting down.");

            // Serialize registration to avoid CAS contention failures
            lock (_registerGate)
            {
                if (_cts.IsCancellationRequested)
                    throw new ObjectDisposedException(nameof(TraceDispatcher), "Dispatcher is shutting down.");
                var snapshot = Volatile.Read(ref _sinks);
                // Avoid closure allocation from Array.Exists; manual scan
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (ReferenceEquals(snapshot[i], sink))
                        return; // already registered
                }

                // Create channel for this sink
                var effectiveMode = fullMode;
                // Prevent unobservable drops (DropOldest) and blocking (Wait)
                if (fullMode == BoundedChannelFullMode.Wait)
                {
                    effectiveMode = BoundedChannelFullMode.DropWrite;
                    if (_metrics is not null)
                    {
                        try
                        {
                            if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                            _metrics.Increment(
                                DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                                new KeyValuePair<string, object>(TagKeys.Field, "dispatcher_fullmode_wait"),
                                new KeyValuePair<string, object>(TagKeys.Policy, fullMode.ToString().ToLowerInvariant()));
                        }
                        catch { /* swallow */ }
                    }
                }
                else if (fullMode == BoundedChannelFullMode.DropOldest)
                {
                    effectiveMode = BoundedChannelFullMode.DropWrite;
                    try
                    {
                        if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                        _metrics?.Increment(
                            DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "dispatcher_fullmode_dropoldest"),
                            new KeyValuePair<string, object>(TagKeys.Policy, fullMode.ToString().ToLowerInvariant()));
                    }
                    catch { /* swallow */ }
                }
                var channel = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(_queueCapacity)
                {
                    SingleWriter = false,
                    SingleReader = true,
                    FullMode = effectiveMode
                });

                // Create a per-sink CTS linked to the dispatcher token
                var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

                var state = new SinkState
                {
                    Channel = channel,
                    Policy = effectiveMode.ToString().ToLowerInvariant(),
                    Depth = 0,
                    Draining = false,
                    Cts = linked
                };

                _states[sink] = state;

                _queues[sink] = channel;
                _sinkPolicies[sink] = state.Policy;
                // Wait and DropOldest policies handled and surfaced above.

                // Build new sink array and publish
                var updated = new ITraceSink[snapshot.Length + 1];
                Array.Copy(snapshot, updated, snapshot.Length);
                updated[^1] = sink;
                Volatile.Write(ref _sinks, updated);

                // Dedicated, continuous consumer for sink queues
                _pumps[sink] = Task.Factory
                    .StartNew(
                        () => PumpAsync(sink, channel, linked.Token),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .Unwrap();

                // Surface registration as a metric (trace emission handled at bootstrap)
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.DispatcherSinkRegistered);
                    _metrics?.Increment(
                        DiagCoreMetrics.DispatcherSinkRegistered.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                        new KeyValuePair<string, object>(TagKeys.Policy, state.Policy));
                }
                catch { /* swallow */ }

            }
        }

        public void Dispatch(TraceEvent evt)
        {
            if (evt is null) throw new ArgumentNullException(nameof(evt));

            // Enforce redaction/validation boundary if enabled
            if (_enforceRedaction && !evt.IsSanitized)
            {
                try
                {
                    _metrics?.Increment(
                        DiagCoreMetrics.TracesDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.Unredacted),
                        new KeyValuePair<string, object>(TagKeys.Component, evt.Component),
                        new KeyValuePair<string, object>(TagKeys.Operation, evt.Operation));
                }
                catch { /* swallow */ }
                return;
            }

            // Hard-stop after disposal; account the drop once per call
            if (_disposed || _cts.IsCancellationRequested)
            {
                try
                {
                    var sinks = Volatile.Read(ref _sinks);
                    // Attribute drop per sink for clearer operator signals
                    for (int i = 0; i < sinks.Length; i++)
                    {
                        var sinkType = sinks[i].GetType().Name;
                        var policy = _sinkPolicies.TryGetValue(sinks[i], out var pol) ? pol : "unknown";
                        _metrics?.Increment(
                            DiagCoreMetrics.TracesDropped.Name,
                            1,
                            new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.DispatcherDisposed),
                            new KeyValuePair<string, object>(TagKeys.Sink, sinkType),
                            new KeyValuePair<string, object>(TagKeys.Policy, policy));
                    }
                    if (sinks.Length == 0)
                        _metrics?.Increment(DiagCoreMetrics.TracesDropped.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.DispatcherDisposed));
                }
                catch { /* swallow */ }
                return;
            }
            var snapshot = Volatile.Read(ref _sinks);

            // No sinks registered: drop with precise cause to surface misconfiguration early.
            if (snapshot.Length == 0)
            {
                try
                {
                    if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.TracesDropped);
                    _metrics?.Increment(
                        DiagCoreMetrics.TracesDropped.Name,
                        1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.UnregisteredSink),
                        // Keep cardinality low; avoid tagging a dynamic sink name here.
                        new KeyValuePair<string, object>(TagKeys.Field, "no_sinks"));
                }
                catch { /* swallow */ }
                return;
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (_states.TryGetValue(snapshot[i], out var st))
                {
                    if (st.Channel.Writer.TryWrite(evt))
                    {
                        Interlocked.Increment(ref st.Depth);
                    }
                    else
                    {
                        var policy = st.Policy;
                        var sinkType = snapshot[i].GetType().Name;
                        // Distinguish between a full queue and a closed channel (pump fault/teardown)
                        if (st.Draining)
                        {
                            // Suppress noisy faults during controlled drain/unregister/dispose
                            // Backlog is accounted at shutdown via DispatcherShutdownQueueDepth/ShutdownBacklog
                        }
                        else if (st.Channel.Reader.Completion.IsCompleted)
                        {
                            _metrics?.Increment(
                                Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                                1,
                                new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, DropCauses.PumpFault),
                                new KeyValuePair<string, object>(TagKeys.Sink, sinkType),
                                new KeyValuePair<string, object>(TagKeys.Policy, policy));
                        }
                        else
                        {
                            _metrics?.Increment(
                                Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                                1,
                                new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, DropCauses.QueueFull),
                                new KeyValuePair<string, object>(TagKeys.Policy, policy),
                                new KeyValuePair<string, object>(TagKeys.Sink, sinkType));
                        }
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
                            Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, DropCauses.UnregisteredSink),
                        new KeyValuePair<string, object>(TagKeys.Sink, sinkType),
                        new KeyValuePair<string, object>(TagKeys.Field, "unexpected_state"),
                        // Tag as reconfigure to distinguish from genuine misconfiguration
                        new KeyValuePair<string, object>(TagKeys.Policy, "reconfigure"));
                    // Optionally: direct emit if you still want to support legacy sinks
                    // try { snapshot[i].Emit(evt); } catch { /* swallow */ }
                }
            }
        }

        private async Task PumpAsync(ITraceSink sink, Channel<TraceEvent> channel, CancellationToken ct)
        {
            try
            {
                int consecutiveErrors = 0;
                await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                {
                    var startTs = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        sink.Emit(evt);
                        consecutiveErrors = 0;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            _metrics?.Increment(
                                DiagCoreMetrics.TracesDropped.Name,
                                1,
                                new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.SinkError),
                                new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                                new KeyValuePair<string, object>(TagKeys.Exception, ex.GetType().Name));
                            consecutiveErrors++;
                            if (consecutiveErrors == 5)
                            {
                                // Soft signal that a sink is persistently faulting
                                _metrics?.Increment(
                                    DiagCoreMetrics.SinkEmitLong.Name,
                                    1,
                                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                                    new KeyValuePair<string, object>(TagKeys.Policy, "emit_circuit_warning"));
                            }
                        }
                        catch { /* swallow */ }
                        // Tiny backoff to avoid hot-looping on a dead sink
                        if (consecutiveErrors >= 5)
                            await Task.Delay(10, ct).ConfigureAwait(false);

                    }
                    finally
                    {
                        if (_states.TryGetValue(sink, out var st))
                        {
                            Interlocked.Decrement(ref st.Depth);
                        }
                        try
                        {
                            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTs);
                            var elapsedMs = elapsed.TotalMilliseconds;
                            _metrics?.Record(
                                DiagCoreMetrics.SinkEmitDurationMs.Name,
                                elapsedMs,
                                new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name));
                            if (elapsedMs > 1000)

                                _metrics?.Increment(
                                    DiagCoreMetrics.SinkEmitLong.Name,
                                    1,
                                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name));
                        }
                        catch { /* swallow exporter/disposal errors */ }
                    }                
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown via cancellation; do not surface as a fault.
            }
            catch (Exception ex)
            {
                // Pump fault: record once on termination to surface operator signal
                try
                {
                    _metrics?.Increment(
                     Silica.DiagnosticsCore.Metrics.DiagCoreMetrics.TracesDropped.Name,
                    1,
                    new KeyValuePair<string, object>(Silica.DiagnosticsCore.Metrics.TagKeys.DropCause, DropCauses.PumpFault),
                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                    new KeyValuePair<string, object>(TagKeys.Exception, ex.GetType().Name));
                }
                catch { /* swallow */ }            
            }
        }


        /// <summary>
        /// Unregisters a sink, optionally draining its queue before removal to avoid
        /// "unregistered_sink" noise during controlled reconfiguration.
        /// </summary>
        /// <param name="sink">The sink to unregister.</param>
        /// <param name="drainTimeoutMs">
        /// Maximum time in milliseconds to wait for the pump to finish after completing the channel.
        /// </param>
        /// <returns>True if the sink was found and removed; false otherwise.</returns>
        // In TraceDispatcher.UnregisterSink
        public bool UnregisterSink(ITraceSink sink, int drainTimeoutMs = 0)
        {
            if (sink is null) throw new ArgumentNullException(nameof(sink));
            lock (_registerGate)
            {
                if (!_pumps.TryGetValue(sink, out var pumpTask))
                    return false;

                // Clamp and surface negative timeouts
                if (drainTimeoutMs < 0)
                {
                    try
                    {
                        if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                        _metrics?.Increment(
                            DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Field, "dispatcher_unregister_drain_timeout_negative"),
                            new KeyValuePair<string, object>(TagKeys.Policy, drainTimeoutMs.ToString()));
                    }
                    catch { /* swallow */ }
                    drainTimeoutMs = 0;
                }


                // Publish new snapshot without the sink
                var snapshot = Volatile.Read(ref _sinks);
                // Pre-size for all but the removed sink to avoid growth
                var list = new List<ITraceSink>(snapshot.Length > 0 ? snapshot.Length - 1 : 0);
                foreach (var s in snapshot)
                    if (!ReferenceEquals(s, sink))
                        list.Add(s);
                Volatile.Write(ref _sinks, list.ToArray());

                // Complete writer to let pump drain
                if (_states.TryGetValue(sink, out var st))
                {
                    st.Draining = true;
                    st.Channel.Writer.TryComplete();
                    // Surface unregistration as a metric
                    try
                    {
                        if (_metrics is MetricsFacade mf) mf.TryRegister(DiagCoreMetrics.DispatcherSinkUnregistered);
                        _metrics?.Increment(
                            DiagCoreMetrics.DispatcherSinkUnregistered.Name, 1,
                            new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                            new KeyValuePair<string, object>(TagKeys.Policy, st.Policy));
                    }
                    catch { /* swallow */ }
                }

                // Cleanup AFTER pump finishes
                if (drainTimeoutMs > 0)
                {
                    try
                    {
                        pumpTask.Wait(TimeSpan.FromMilliseconds(drainTimeoutMs));
                    }
                    catch { /* swallow */ }
                }

                // If the pump is still not done after optional wait, cancel this sink's pump explicitly
                if (!pumpTask.IsCompleted && _states.TryGetValue(sink, out var stCancel))
                {
                    try { stCancel.Cts.Cancel(); } catch { /* swallow */ }
                }

                pumpTask.ContinueWith(_ =>
                {
                    if (_states.TryRemove(sink, out var removed))
                    {
                        try { removed.Cts.Dispose(); } catch { /* swallow */ }
                    }

                    _queues.TryRemove(sink, out Channel<TraceEvent> _);       // ✅ Channel<TraceEvent>
                    _sinkPolicies.TryRemove(sink, out string _);              // ✅ string
                    _pumps.TryRemove(sink, out Task _);                        // ✅ Task
                    // Dispose sink if it owns resources
                    try
                    {
                        if (sink is IAsyncDisposable iad)
                            iad.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        else if (sink is IDisposable d)
                            d.Dispose();
                    }
                    catch { /* swallow sink disposal errors */ }
                }, TaskScheduler.Default);

                return true;
            }
        }


        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 1) Stop producing: complete writers first so pumps can drain naturally
            foreach (var kv in _states)
                kv.Value.Draining = true;
            foreach (var ch in _queues.Values)
                ch.Writer.TryComplete();

            // 2) Best-effort drain within timeout (no cancellation yet)
            bool drained = false;
            try
            {
                drained = Task.WhenAll(_pumps.Values).Wait(TimeSpan.FromMilliseconds(_shutdownTimeoutMs));
            }
            catch
            {
                // accounted below
            }

            // 3) If not drained, cancel pumps to break out and account backlog
            if (!drained)
            {
                // Cancel per-sink first to allow fine-grained teardown
                foreach (var kv in _states)
                {
                    try { kv.Value.Cts.Cancel(); } catch { /* swallow */ }
                }
                try { _cts.Cancel(); } catch { /* swallow */ }
                try { Task.WhenAll(_pumps.Values).Wait(TimeSpan.FromMilliseconds(100)); } catch { /* swallow */ }
            }

            // 4) Surface remaining pumps and backlog when not fully drained
            if (!drained && _metrics is not null)
            {
                try
                {
                    _metrics.Record(DiagCoreMetrics.PumpsRemainingOnTimeout.Name, _pumps.Count);

                    long depth = 0;
                    foreach (var kv in _states)
                        depth += Interlocked.Read(ref kv.Value.Depth);
                    _metrics.Record(DiagCoreMetrics.DispatcherShutdownQueueDepth.Name, depth);
                    // Optional: emit a trace if you wire an emitter in Bootstrap
                    // (left as metrics-only; trace emission belongs in bootstrap’s Stop path if desired)
                    foreach (var kv in _states)
                    {
                        var remaining = Interlocked.Read(ref kv.Value.Depth);
                        if (remaining > 0)
                        {
                            var sink = kv.Key;
                            var policy = kv.Value.Policy;
                            try
                            {
                                _metrics.Increment(
                                    DiagCoreMetrics.TracesDropped.Name, remaining,
                                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.ShutdownBacklog),
                                    new KeyValuePair<string, object>(TagKeys.Sink, sink.GetType().Name),
                                    new KeyValuePair<string, object>(TagKeys.Policy, policy));
                            }
                            catch { /* swallow */ }
                        }
                    }
                }
                catch { /* swallow exporter/disposal errors */ }
            }

            // 4b) Dispose sinks we own (after pumps have completed/canceled)
            var sinksSnapshot = Volatile.Read(ref _sinks);
            foreach (var s in sinksSnapshot)
            {
                try
                {
                    if (s is IAsyncDisposable iad)
                        iad.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    else if (s is IDisposable d)
                        d.Dispose();
                }
                catch { /* swallow sink disposal errors */ }
            }


            // 5) Cleanup
            // Dispose per-sink CTS
            foreach (var kv in _states)
            {
                try { kv.Value.Cts.Dispose(); } catch { /* swallow */ }
            }
            try { _cts.Dispose(); } catch { /* swallow */ }
            _states.Clear();
            _queues.Clear();
            _pumps.Clear();
            _sinkPolicies.Clear();
            _sinks = Array.Empty<ITraceSink>();
        }
    }
}
