using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Enrichers
{
    /// <summary>
    /// Enricher that merges the AmbientTagContext into trace tags (last-writer wins).
    /// Does not throw; ignores invalid keys/values.
    /// </summary>
    public sealed class AmbientTagEnricher : IDiagnosticsEnricher
    {
        public void Enrich(IDictionary<string, string> tags)
        {
            if (tags == null) return;
            var ambient = AmbientTagContext.Snapshot();
            if (ambient == null || ambient.Count == 0) return;

            var e = ambient.GetEnumerator();
            try
            {
                while (e.MoveNext())
                {
                    var kv = e.Current;
                    if (kv.Key == null) continue;
                    var k = kv.Key.Trim();
                    if (k.Length == 0) continue;
                    var v = kv.Value ?? string.Empty;
                    tags[k] = v; // last-writer wins
                }
            }
            finally { e.Dispose(); }
        }
    }
}
