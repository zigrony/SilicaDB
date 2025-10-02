using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Tracing;

namespace Silica.StorageAllocation.Sql.Diagnostics
{
    /// <summary>
    /// Canonical diagnostics surface for SQL allocation strategy.
    /// Mirrors the pattern used in ConcurrencyDiagnostics.
    /// </summary>
    internal static class SqlAllocationDiagnostics
    {
        public static volatile bool EnableVerbose = false;

        static SqlAllocationDiagnostics()
        {
            try { ReloadFromEnvironment(); } catch { }
        }

        internal static void SetVerbose(bool on) => EnableVerbose = on;

        internal static void ReloadFromEnvironment()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("SILICA_SQLALLOC_VERBOSE");
                if (string.IsNullOrEmpty(v)) return;
                var s = v.Trim();
                if (s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    EnableVerbose = true; return;
                }
                if (s.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    EnableVerbose = false; return;
                }
            }
            catch { }
        }

        internal static void Emit(
            string operation,
            string level,
            string message,
            Exception? ex = null,
            IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;

            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; }
            catch { return; }

            var tags = new Dictionary<string, string>(
                more is null ? 2 : 2 + more.Count,
                StringComparer.OrdinalIgnoreCase);

            const string componentName = "Silica.StorageAllocation.Sql";
            string operationName = operation ?? string.Empty;

            tags["component"] = componentName;
            tags["operation"] = operationName;

            if (more is not null)
            {
                foreach (var kv in more)
                {
                    if (string.Equals(kv.Key, "component", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "operation", StringComparison.OrdinalIgnoreCase)) continue;
                    tags[kv.Key] = kv.Value;
                }
            }

            var lvl = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim();
            if (string.Equals(lvl, "DEBUG", StringComparison.OrdinalIgnoreCase)) lvl = "debug";
            else if (string.Equals(lvl, "INFO", StringComparison.OrdinalIgnoreCase)) lvl = "info";
            else if (string.Equals(lvl, "WARN", StringComparison.OrdinalIgnoreCase)) lvl = "warn";
            else if (string.Equals(lvl, "ERROR", StringComparison.OrdinalIgnoreCase)) lvl = "error";
            else if (string.Equals(lvl, "FATAL", StringComparison.OrdinalIgnoreCase)) lvl = "error";
            else if (string.Equals(lvl, "TRACE", StringComparison.OrdinalIgnoreCase)) lvl = "debug";

            try { traces.Emit(componentName, operationName, lvl, tags, message ?? string.Empty, ex); }
            catch { }
        }
    }
}
