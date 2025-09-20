// Filename: Silica.BufferPool/Diagnostics/BufferPoolDiagnostics.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions;

namespace Silica.BufferPool.Diagnostics
{
    internal static class BufferPoolDiagnostics
    {
        // Defaults to quiet; can be enabled via env or host.
        public static volatile bool EnableVerbose = false;

        static BufferPoolDiagnostics()
        {
            try { ReloadFromEnvironment(); } catch { }
        }

        internal static void SetVerbose(bool on) => EnableVerbose = on;

        internal static void ReloadFromEnvironment()
        {
            try
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_BUFFERPOOL_VERBOSE"); } catch { }
                if (!string.IsNullOrEmpty(v))
                {
                    var s = v.Trim();
                    if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        EnableVerbose = true;
                        return;
                    }
                    if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "no", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        EnableVerbose = false;
                        return;
                    }
                }
                // leave current value if unset or unparsable
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

            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; }
            catch { return; }

            var tags = new Dictionary<string, string>(more is null ? 2 : (2 + more.Count), StringComparer.OrdinalIgnoreCase);
            string componentName = "Silica.BufferPool";
            string operationName = operation ?? string.Empty;

            try
            {
                componentName = string.IsNullOrWhiteSpace(component) ? "Silica.BufferPool" : component;
                tags["component"] = componentName;
                tags["operation"] = operationName;

                if (more is not null)
                {
                    var e = more.GetEnumerator();
                    try
                    {
                        while (e.MoveNext())
                        {
                            var kv = e.Current;
                            if (string.Equals(kv.Key, "component", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(kv.Key, "operation", StringComparison.OrdinalIgnoreCase)) continue;
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
                else if (string.Equals(lvl, "FATAL", StringComparison.OrdinalIgnoreCase)) lvl = "error";
                else if (string.Equals(lvl, "TRACE", StringComparison.OrdinalIgnoreCase)) lvl = "debug";

                try
                {
                    var se = ex as SilicaException;
                    if (se != null)
                    {
                        tags["error.code"] = se.Code ?? string.Empty;
                        tags["exception.id"] = se.ExceptionId.ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch { }

                traces.Emit(componentName, operationName, lvl, tags, message ?? string.Empty, ex);
            }
            catch { }
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
