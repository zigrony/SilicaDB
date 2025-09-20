// Filename: Silica.Evictions/Diagnostics/EvictionDiagnostics.cs
using System;
using System.Collections.Generic;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions;

namespace Silica.Evictions.Diagnostics
{
    internal static class EvictionDiagnostics
    {
        // Defaults to quiet; can be enabled via env or host.
        public static volatile bool EnableVerbose = false;

        static EvictionDiagnostics()
        {
            try
            {
                string? v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_EVICTIONS_VERBOSE"); } catch { }
                if (!string.IsNullOrEmpty(v))
                {
                    var s = v.Trim();
                    if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        EnableVerbose = true;
                    }
                }
            }
            catch { }
        }

        internal static void Emit(string component, string operation, string level, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; }
            catch { return; }

            var tags = new Dictionary<string, string>(more is null ? 2 : (2 + more.Count), StringComparer.OrdinalIgnoreCase);
            try
            {
                tags[TagKeys.Component] = string.IsNullOrWhiteSpace(component) ? "Silica.Evictions" : component;
                tags[TagKeys.Operation] = operation ?? string.Empty;
                if (more is not null)
                {
                    var e = more.GetEnumerator();
                    try
                    {
                        while (e.MoveNext())
                        {
                            var kv = e.Current;
                            // do not allow overwrite of reserved keys
                            if (string.Equals(kv.Key, TagKeys.Component, StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(kv.Key, TagKeys.Operation, StringComparison.OrdinalIgnoreCase)) continue;
                            tags[kv.Key] = kv.Value;
                        }
                    }
                    finally { e.Dispose(); }
                }
            }
            catch { }
            try
            {
                var lvl = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim();
                if (string.Equals(lvl, "DEBUG", StringComparison.OrdinalIgnoreCase)) lvl = "debug";
                else if (string.Equals(lvl, "INFO", StringComparison.OrdinalIgnoreCase)) lvl = "info";
                else if (string.Equals(lvl, "WARN", StringComparison.OrdinalIgnoreCase) || string.Equals(lvl, "WARNING", StringComparison.OrdinalIgnoreCase)) lvl = "warn";
                else if (string.Equals(lvl, "ERROR", StringComparison.OrdinalIgnoreCase)) lvl = "error";

                // Stamp structured identity when possible
                try
                {
                    var se = ex as SilicaException;
                    if (se != null)
                    {
                        tags["error.code"] = se.Code ?? string.Empty;
                        tags["exception.id"] = se.ExceptionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch { }
                traces.Emit(tags[TagKeys.Component], operation ?? string.Empty, lvl, tags, message ?? string.Empty, ex);
            }
            catch { }
        }

        internal static void EmitDebug(string component, string operation, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null, bool allowDebug = false)
        {
            if (!EnableVerbose && !allowDebug) return;
            Emit(component, operation, "debug", message, ex, more);
        }
    }
}
