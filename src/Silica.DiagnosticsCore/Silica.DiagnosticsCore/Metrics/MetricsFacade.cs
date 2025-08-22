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
        private volatile bool _warnedStrictLenientNoAutoReg;
        public void AddAllowedTagKeys(IEnumerable<string> keys)
        {
            _tagValidator.AddAllowedKeys(keys);
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
        }

        public void Register(MetricDefinition definition)
        {
            if (definition is null)
                throw new ArgumentNullException(nameof(definition));

            definition.EnsureValid();
            _registry.Register(definition);
            _inner.Register(definition);
        }

        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags)
        {
            PublishIfAllowed(name, () =>
                _inner.Increment(name, value, _tagValidator.Validate(Merge(tags)))); // CHG
        }

        public void Record(string name, double value, params KeyValuePair<string, object>[] tags)
        {
            PublishIfAllowed(name, () =>
                _inner.Record(name, value, _tagValidator.Validate(Merge(tags)))); // CHG
        }

        private void PublishIfAllowed(string name, Action publish)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Metric name must not be null or whitespace.", nameof(name));

            if (!_registry.IsKnown(name))
            {
               // Account only when it will actually be dropped:
               // - strict mode (blocked here), or
               // - inner is MetricsManager (no auto-registration; emits nothing).
               if (_strict || _inner is MetricsManager)
                   _onDrop?.Invoke(name);

               if (_strict) return;

               // Warn once in lenient mode. Distinguish unknown inner with a tag.
               // Note: publish() is still called below. If _inner is MetricsManager,
               // it will no-op on unknown metrics; the single 'lenient_no_autoreg'
               // counter surfaces that behavior to operators.
               if (!_warnedStrictLenientNoAutoReg)
               {
                   _warnedStrictLenientNoAutoReg = true;
                   try
                   {
                       var tags = _inner is MetricsManager
                           ? Array.Empty<KeyValuePair<string, object>>()
                           : new[] { new KeyValuePair<string, object>(TagKeys.Field, "unknown_inner_autoreg") };
                       _inner.Increment(DiagCoreMetrics.MetricsLenientNoAutoreg.Name, 1, tags);
                   }
                   catch { /* no-throw */ }
               }              
            }
            publish();
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
            if (_ownsInner && _inner is IDisposable d)
               d.Dispose();
        }
        public void TryRegister(MetricDefinition definition)
        {
            if (!_registry.IsKnown(definition.Name))
                Register(definition);
        }
    }
}
