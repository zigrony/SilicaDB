// Filename: Silica.PageAccess/Diagnostics/PageAccessDiagnostics.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Exceptions;

namespace Silica.PageAccess.Diagnostics
{
    internal static class PageAccessDiagnostics
    {
        // Defaults to quiet; can be enabled via env or host.
        public static volatile bool EnableVerbose = false;

        static PageAccessDiagnostics()
        {
            try { ReloadFromEnvironment(); } catch { }
        }

        internal static void SetVerbose(bool on) => EnableVerbose = on;

        internal static void ReloadFromEnvironment()
        {
            try
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_PAGEACCESS_VERBOSE"); } catch { }
                if (!string.IsNullOrEmpty(v))
                {
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
            }
            catch { }
        }

        internal static void Emit(
            string component,
            string operation,
            string level,
            string message,
            Exception? ex = null,
            IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            var traces = DiagnosticsCoreBootstrap.Instance.Traces;

            var tags = new Dictionary<string, string>(
                more is null ? 2 : 2 + more.Count,
                StringComparer.OrdinalIgnoreCase);

            string componentName = string.IsNullOrWhiteSpace(component) ? "Silica.PageAccess" : component;
            string operationName = operation ?? string.Empty;

            tags[TagKeys.Component] = componentName;
            tags[TagKeys.Operation] = operationName;

            if (more is not null)
            {
                var it = more.GetEnumerator();
                try
                {
                    while (it.MoveNext())
                    {
                        var kv = it.Current;
                        if (kv.Key.Equals(TagKeys.Component, StringComparison.OrdinalIgnoreCase)) continue;
                        if (kv.Key.Equals(TagKeys.Operation, StringComparison.OrdinalIgnoreCase)) continue;
                        tags[kv.Key] = kv.Value;
                    }
                }
                finally { it.Dispose(); }
            }

            // Stamp structured exception identity when available
            try
            {
                if (ex is SilicaException se)
                {
                    tags["error.code"] = se.Code ?? string.Empty;
                    tags["exception.id"] = se.ExceptionId.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }

            // Normalize level (match ConcurrencyDiagnostics behavior)
            var lvl = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim();
            if (string.Equals(lvl, "DEBUG", StringComparison.OrdinalIgnoreCase)) lvl = "debug";
            else if (string.Equals(lvl, "INFO", StringComparison.OrdinalIgnoreCase)) lvl = "info";
            else if (string.Equals(lvl, "WARN", StringComparison.OrdinalIgnoreCase) || string.Equals(lvl, "WARNING", StringComparison.OrdinalIgnoreCase)) lvl = "warn";
            else if (string.Equals(lvl, "ERROR", StringComparison.OrdinalIgnoreCase)) lvl = "error";
            else if (string.Equals(lvl, "FATAL", StringComparison.OrdinalIgnoreCase)) lvl = "error";
            else if (string.Equals(lvl, "TRACE", StringComparison.OrdinalIgnoreCase)) lvl = "debug";

            traces.Emit(componentName, operationName, lvl, tags, message ?? string.Empty, ex);
        }

        internal static void EmitDebug(
            string component,
            string operation,
            string message,
            Exception? ex = null,
            IReadOnlyDictionary<string, string>? more = null,
            bool allowDebug = false)
        {
            if (!EnableVerbose && !allowDebug) return;
            Emit(component, operation, "debug", message, ex, more);
        }
    }
}
