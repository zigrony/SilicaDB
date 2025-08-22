// File: Silica.DiagnosticsCore/Tracing/TraceCategory.cs
using System;
using System.Collections.Generic;

namespace Silica.DiagnosticsCore.Tracing
{
    /// <summary>
    /// Well‑known categories for trace events, used to classify and filter output
    /// in tracing, logging, and analysis pipelines.
    /// </summary>
    public static class TraceCategory
    {
        public const string IO = "io";
        public const string WAL = "wal";
        public const string Storage = "storage";
        public const string BufferPool = "bufferpool";
        public const string Eviction = "eviction";
        public const string Observability = "observability";
        public const string Api = "api";
        public const string Maintenance = "maintenance";
        public const string Other = "other";

        // Centralized lookup for IsKnown; avoids long chains of comparisons
        private static readonly HashSet<string> Known =
            new(StringComparer.OrdinalIgnoreCase)
            {
                IO, WAL, Storage, BufferPool, Eviction,
                Observability, Api, Maintenance, Other
            };

        /// <summary>
        /// Checks if the provided category string matches one of the well‑known values.
        /// Case‑insensitive; null/whitespace returns false.
        /// </summary>
        public static bool IsKnown(string category) =>
            !string.IsNullOrWhiteSpace(category) && Known.Contains(category);
    }
}
