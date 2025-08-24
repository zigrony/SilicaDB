// File: Silica.DiagnosticsCore/Metrics/MetricsFacade.cs
using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Wraps an <see cref="IMetricsManager"/> to enforce registry membership,
    /// schema validation, and optional drop‑accounting before delegating emissions.
    /// </summary>
    public sealed class MetricsFacade : IMetricsManager, IDisposable
    {
        private readonly IMetricsManager _inner;
        private readonly bool _ownsInner;
        private readonly MetricRegistry _registry;
        private readonly TagValidator _tagValidator;
        private readonly bool _strict;
        private readonly Action<string>? _onDrop;
        private readonly IReadOnlyDictionary<string, string>? _globalTags;
        // Optional: surface a single warning metric when strict=false is used with a manager that doesn't auto-register
        private volatile bool _warnedLenientNoAutoReg;
        private volatile bool _warnedLenientAutoReg;
        private volatile bool _disposed;

        public void AddAllowedTagKeys(IEnumerable<string> keys)
        {
            _tagValidator.AddAllowedKeys(keys);
           if (_inner is not NoOpMetricsManager)
               try
               {
                   TryRegister(DiagCoreMetrics.IgnoredConfigFieldSet);
                   _inner.Increment(DiagCoreMetrics.IgnoredConfigFieldSet.Name, 1,
                       new KeyValuePair<string, object>(TagKeys.Field, "dynamic_tag_allowlist"));
               }
               catch { }
        }

    /// <param name="inner">The underlying metrics manager to emit to.</param>
    /// <param name="registry">Canonical registry of metric definitions.</param>
    /// <param name="strict">
    /// If true, emissions to unknown metrics are rejected (and optionally dropped).
    /// If false, emissions may be auto‑registered by the underlying manager.
    /// </param>
    /// <param name="onDrop">
    /// Optional callback invoked with the metric name whenever an emission is dropped due to strict mode.
    /// </param>
    public MetricsFacade(
            IMetricsManager inner,
            MetricRegistry registry,
            TagValidator tagValidator,
            bool strict = true,
            Action<string>? onDrop = null,
            IReadOnlyDictionary<string, string>? globalTags = null,
            bool ownsInner = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _tagValidator = tagValidator ?? throw new ArgumentNullException(nameof(tagValidator));
            _strict = strict;
            _onDrop = onDrop;
            _globalTags = globalTags;
            _ownsInner = ownsInner;
            // Wire tag drop/truncation accounting for metrics emissions.
            // Note: No-op when metrics are globally disabled via NoOpMetricsManager.
            _tagValidator.SetCallbacks(
                onTagRejectedCount: count =>
                {
                    if (_inner is NoOpMetricsManager) return;
                    try
                    {
                        TryRegister(DiagCoreMetrics.MetricTagsRejected);
                        _inner.Increment(DiagCoreMetrics.MetricTagsRejected.Name, count);
                    }
                    catch { /* swallow exporter/registration issues */ }
                },
                onTagTruncatedCount: count =>
                {
                    if (_inner is NoOpMetricsManager) return;
                    try
                    {
                        TryRegister(DiagCoreMetrics.MetricTagsTruncated);
                        _inner.Increment(DiagCoreMetrics.MetricTagsTruncated.Name, count);
                    }
                    catch { /* swallow exporter/registration issues */ }
                });

        }

        public void Register(MetricDefinition definition)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MetricsFacade));

            if (definition is null)
                throw new ArgumentNullException(nameof(definition));

            definition.EnsureValid();
            _registry.Register(definition);
            _inner.Register(definition);
        }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            if (!VerifyTypeOrDrop(name, MetricType.Counter)) return;
            PublishIfAllowed(name, () =>
                _inner.Increment(name, value, _tagValidator.Validate(Merge(tags)))); // CHG
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            if (!VerifyTypeOrDrop(name, MetricType.Histogram)) return;
            PublishIfAllowed(name, () =>
                _inner.Record(name, value, _tagValidator.Validate(Merge(tags)))); // CHG
        }

        private void PublishIfAllowed(string name, Action publish)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Metric name must not be null or whitespace.", nameof(name));

            // Metrics disabled: avoid overhead and any misleading accounting/warnings.
            if (_inner is NoOpMetricsManager) return;

            if (!_registry.IsKnown(name))
            {
                // Strict mode: drop and account precisely
                if (_strict)
                {
                    CountDrop(DropCauses.UnknownMetricStrict);
                    return;
                }

                // Lenient mode:
                if (_inner is MetricsManager)
                {
                    // Inner cannot auto-register; drop and warn once
                    if (!_warnedLenientNoAutoReg)
                    {
                        _warnedLenientNoAutoReg = true;
                        try
                        {
                            TryRegister(DiagCoreMetrics.MetricsLenientNoAutoreg);
                            _inner.Increment(DiagCoreMetrics.MetricsLenientNoAutoreg.Name, 1);
                        }
                        catch { /* no-throw */ }
                    }
                    CountDrop(DropCauses.UnknownMetricNoAutoreg);
                    return;
                }

                // Inner may auto-register (schema drift risk) - surface once
                if (!_warnedLenientAutoReg)
                {
                    _warnedLenientAutoReg = true;
                    try
                    {
                        TryRegister(DiagCoreMetrics.MetricsLenientAutoregUsed);
                        _inner.Increment(DiagCoreMetrics.MetricsLenientAutoregUsed.Name, 1);
                    }
                    catch { /* no-throw */ }
                }            
            }
           try { publish(); }
           catch (ObjectDisposedException)
           {
               // Inner already disposed; account late emission and return.
               try
               {
                   TryRegister(DiagCoreMetrics.MetricsDropped);
                   _inner.Increment(
                       DiagCoreMetrics.MetricsDropped.Name, 1,
                       new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InnerDisposed));
                }
               catch { /* swallow */ }
               _onDrop?.Invoke(name);
           }
           catch (InvalidOperationException)
           {
               try
               {
                   TryRegister(DiagCoreMetrics.MetricsDropped);
                   _inner.Increment(
                       DiagCoreMetrics.MetricsDropped.Name, 1,
                       new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.InnerError));
               }
               catch { /* swallow */ }
               _onDrop?.Invoke(name);
           }
           catch (Exception)
           {
               DropInner(DropCauses.InnerException);
           }

            void DropInner(string cause)
            {
                try
                {
                    TryRegister(DiagCoreMetrics.MetricsDropped);
                    _inner.Increment(DiagCoreMetrics.MetricsDropped.Name, 1,
                        new KeyValuePair<string, object>(TagKeys.DropCause, cause));
                }
                catch { /* swallow */ }
                _onDrop?.Invoke(name);
            }
            void CountDrop(string cause)
            {
               try
               {
                   TryRegister(DiagCoreMetrics.MetricsDropped);
                   _inner.Increment(
                       DiagCoreMetrics.MetricsDropped.Name, 1,
                       new KeyValuePair<string, object>(TagKeys.DropCause, cause));
               }
               catch { /* swallow */ }
               _onDrop?.Invoke(name);
           }            
        }

        // Verify that the metric is registered with the expected type; if mismatched,
        // account a drop and prevent a silent no-op.
        private bool VerifyTypeOrDrop(string name, MetricType expectedType)
        {
            try
            {
                // If unknown, allow PublishIfAllowed to handle strict/lenient paths.
                if (!_registry.IsKnown(name)) return true;
                var def = _registry.GetDefinition(name);
                if (def.Type == expectedType) return true;
            }
            catch
            {
                // Registry may throw for edge conditions; defer to publish path.
                return true;
            }

            // Known metric with mismatched type: count a precise drop.
            try
            {
                TryRegister(DiagCoreMetrics.MetricsDropped);
                _inner.Increment(
                    DiagCoreMetrics.MetricsDropped.Name, 1,
                    new KeyValuePair<string, object>(TagKeys.DropCause, DropCauses.TypeMismatch));
            }
            catch { /* swallow */ }
            _onDrop?.Invoke(name);
            return false;
        }

        private KeyValuePair<string, object>[] Merge(KeyValuePair<string, object>[]? tags)
        {
            if ((_globalTags is null || _globalTags.Count == 0) && (tags is null || tags.Length == 0))
                return Array.Empty<KeyValuePair<string, object>>();

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (_globalTags is { Count: > 0 })
                foreach (var kv in _globalTags) dict[kv.Key] = kv.Value;
            if (tags is { Length: > 0 })
                for (int i = 0; i < tags.Length; i++)
                {
                    var (k, v) = tags[i];
                    if (!string.IsNullOrWhiteSpace(k))
                        dict[k] = v; // last-write-wins
                }
            if (dict.Count == 0) return Array.Empty<KeyValuePair<string, object>>();
            var merged = new KeyValuePair<string, object>[dict.Count];
            int idx = 0;
            foreach (var kv in dict) merged[idx++] = kv;
            return merged;
        }

        public void Dispose()
        {
        if (_disposed) return;
        _disposed = true;
        if (_ownsInner && _inner is IDisposable d) d.Dispose();
        }
        public void TryRegister(MetricDefinition definition)
        {
            if (!_registry.IsKnown(definition.Name))
                Register(definition);
        }
    }
}
