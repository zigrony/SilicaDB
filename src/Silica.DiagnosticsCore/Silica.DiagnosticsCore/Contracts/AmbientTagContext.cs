using System;
using System.Collections.Generic;
using System.Threading;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Ambient tag context for cross-cutting enrichment without hard dependencies.
    /// Subsystems can push tags (e.g., session.id) into this context; the AmbientTagEnricher
    /// will merge them into every emitted trace.
    /// </summary>
    public static class AmbientTagContext
    {
        // AsyncLocal to flow across async calls within a logical operation.
        private static readonly AsyncLocal<Dictionary<string, string>?> _current = new AsyncLocal<Dictionary<string, string>?>();

        /// <summary>
        /// Set or update a tag in the ambient context. Null key is ignored; empty trimmed key ignored.
        /// If value is null, the tag is removed.
        /// </summary>
        public static void Set(string key, string? value)
        {
            if (key == null) return;
            var trimmed = key.Trim();
            if (trimmed.Length == 0) return;

            var dict = _current.Value;
            if (dict == null)
            {
                if (value == null) return; // nothing to remove
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _current.Value = dict;
            }
            if (value == null)
            {
                if (dict.ContainsKey(trimmed)) dict.Remove(trimmed);
                return;
            }
            dict[trimmed] = value;
        }

        /// <summary>
        /// Remove a tag from the ambient context.
        /// </summary>
        public static void Remove(string key)
        {
            if (key == null) return;
            var trimmed = key.Trim();
            if (trimmed.Length == 0) return;
            var dict = _current.Value;
            if (dict == null) return;
            if (dict.ContainsKey(trimmed)) dict.Remove(trimmed);
        }

        /// <summary>
        /// Clears all ambient tags for the current logical flow.
        /// </summary>
        public static void Clear()
        {
            _current.Value = null;
        }

        /// <summary>
        /// Returns a copy of the ambient tags, or null when none present.
        /// </summary>
        internal static Dictionary<string, string>? Snapshot()
        {
            var dict = _current.Value;
            if (dict == null || dict.Count == 0) return null;
            // Copy without LINQ
            var copy = new Dictionary<string, string>(dict.Count, StringComparer.OrdinalIgnoreCase);
            var e = dict.GetEnumerator();
            try
            {
                while (e.MoveNext())
                {
                    var kv = e.Current;
                    copy[kv.Key] = kv.Value;
                }
            }
            finally { e.Dispose(); }
            return copy;
        }
    }
}
