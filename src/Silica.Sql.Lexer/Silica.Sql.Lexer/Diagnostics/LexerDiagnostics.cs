using System;
using System.Collections.Generic;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions;

namespace Silica.Sql.Lexer.Diagnostics
{
    internal static class LexerDiagnostics
    {
        private const string Component = "Silica.Sql.Lexer";
        // Optional: gate verbose logs; can be toggled by hosting process.
        public static volatile bool EnableVerbose = false;

        // New: component-aware overload for unified tagging with metrics.
        internal static void Emit(string component, string operation, string level, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            TraceManager traces;
            try
            {
                traces = DiagnosticsCoreBootstrap.Instance.Traces;
            }
            catch
            {
                return;
            }

            // Keep tag cardinality low and stable. Pre-size for reserved + 'more' spill-ins.
            int cap = 4;
            try
            {
                if (more is not null)
                {
                    // Reserve space for 'more' while keeping small dictionary footprint.
                    cap += more.Count;
                }
            }
            catch { /* best-effort sizing only */ }
            var tags = new Dictionary<string, string>(cap, StringComparer.OrdinalIgnoreCase);
            // Normalize and retain component locally so we don't rely on tags indexing later.
            string comp = Component;
            try
            {
                // Prefer caller-supplied component (fallback to library default)
                comp = string.IsNullOrWhiteSpace(component) ? Component : component;
                tags[TagKeys.Component] = comp;
                tags[TagKeys.Operation] = operation ?? string.Empty;
                if (more is not null)
                {
                    // Do not allow callers to overwrite reserved keys.
                    var e = more.GetEnumerator();
                    try
                    {
                        while (e.MoveNext())
                        {
                            var kv = e.Current;
                            if (string.Equals(kv.Key, TagKeys.Component, StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(kv.Key, TagKeys.Operation, StringComparison.OrdinalIgnoreCase)) continue;
                            tags[kv.Key] = kv.Value;
                        }
                    }
                    finally
                    {
                        // Enumerators are IDisposable in general; dispose for hygiene/symmetry.
                        e.Dispose();
                    }
                }
            }
            catch
            {
                // Swallow tag build issues and attempt emit with partial data.
            }
            try
            {
                // Normalize level; always allow info and higher. Gate only debug on verbosity.
                var lvl = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim();
                // canonicalize level casing (stability for downstream sinks)
                if (string.Equals(lvl, "DEBUG", StringComparison.OrdinalIgnoreCase)) lvl = "debug";
                else if (string.Equals(lvl, "INFO", StringComparison.OrdinalIgnoreCase)) lvl = "info";
                else if (string.Equals(lvl, "WARN", StringComparison.OrdinalIgnoreCase) || string.Equals(lvl, "WARNING", StringComparison.OrdinalIgnoreCase)) lvl = "warn";
                else if (string.Equals(lvl, "ERROR", StringComparison.OrdinalIgnoreCase)) lvl = "error";
                if (!EnableVerbose && string.Equals(lvl, "debug", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                // If this is a SilicaException, stamp structured identity tags.
                try
                {
                    var se = ex as SilicaException;
                    if (se != null)
                    {
                        // These keys are not reserved by TagKeys to keep cardinality predictable.
                        tags["error.code"] = se.Code ?? string.Empty;
                        tags["exception.id"] = se.ExceptionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch { /* never fail diagnostics on tag extraction */ }
                traces.Emit(comp, operation ?? string.Empty, lvl, tags, message ?? string.Empty, ex);
            }
            catch
            {
                // Swallow all diagnostics faults.
            }
        }

        // Per-call debug emission that can opt in without flipping global verbosity.
        internal static void EmitDebug(string component, string operation, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null, bool allowDebug = false)
        {
            // Honor either the global toggle or explicit per-call allowance.
            if (!EnableVerbose && !allowDebug) return;
            Emit(component, operation, "debug", message, ex, more);
        }
    }
}
