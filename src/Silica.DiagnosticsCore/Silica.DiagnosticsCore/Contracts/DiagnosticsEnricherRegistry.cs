using System;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Global registry for diagnostics enrichers. Thread-safe, lock-based; no LINQ.
    /// </summary>
    public static class DiagnosticsEnricherRegistry
    {
        private static readonly object _lock = new object();
        private static IDiagnosticsEnricher[] _enrichers = Array.Empty<IDiagnosticsEnricher>();

        /// <summary>
        /// Registers an enricher (append-only). Duplicate instances are allowed; call sites control registration.
        /// </summary>
        public static void Register(IDiagnosticsEnricher enricher)
        {
            if (enricher == null) return;
            lock (_lock)
            {
                var old = _enrichers;
                var arr = new IDiagnosticsEnricher[old.Length + 1];
                for (int i = 0; i < old.Length; i++) arr[i] = old[i];
                arr[old.Length] = enricher;
                _enrichers = arr;
            }
        }

        /// <summary>
        /// Snapshot of currently registered enrichers. No allocation on reads once stabilized.
        /// </summary>
        public static IDiagnosticsEnricher[] GetAll()
        {
            return _enrichers;
        }
    }
}
