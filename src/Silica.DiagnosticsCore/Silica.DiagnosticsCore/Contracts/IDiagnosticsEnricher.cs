using System.Collections.Generic;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Contract for components that enrich trace tag dictionaries with additional context.
    /// Enrichers may add, modify, or remove entries. Last-writer wins semantics apply.
    /// </summary>
    public interface IDiagnosticsEnricher
    {
        /// <summary>
        /// Enrich the tag dictionary in place. Implementations should be defensive:
        /// - Do not throw on null keys or unexpected values; skip gracefully.
        /// - Keep cardinality low and values short (honor DiagnosticsOptions limits).
        /// </summary>
        void Enrich(IDictionary<string, string> tags);
    }
}
