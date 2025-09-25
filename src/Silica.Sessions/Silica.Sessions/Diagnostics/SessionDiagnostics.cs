using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions;

namespace Silica.Sessions.Diagnostics
{
    /// <summary>
    /// Centralized structured tracing for Sessions. Low-cardinality tags, resilient emissions.
    /// </summary>
    internal static class SessionDiagnostics
    {
        public static volatile bool EnableVerbose = false;

        static SessionDiagnostics()
        {
            try { ReloadFromEnvironment(); } catch { }
        }

        internal static void SetVerbose(bool on) => EnableVerbose = on;

        internal static void ReloadFromEnvironment()
        {
            try
            {
                string? v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_SESSIONS_VERBOSE"); } catch { }
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
            string status,
            string message,
            Exception? ex = null,
            IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;

            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; } catch { return; }

            var tags = new Dictionary<string, string>(more is null ? 3 : (3 + more.Count), StringComparer.OrdinalIgnoreCase);
            string componentName = string.IsNullOrWhiteSpace(component) ? "Silica.Sessions" : component;
            string operationName = operation ?? string.Empty;

            try
            {
                tags[TagKeys.Component] = componentName;
                tags[TagKeys.Operation] = operationName;
                tags["status"] = status ?? "info";

                if (more is not null)
                {
                    var e = more.GetEnumerator();
                    try
                    {
                        while (e.MoveNext())
                        {
                            var kv = e.Current;
                            if (string.Equals(kv.Key, TagKeys.Component, StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(kv.Key, TagKeys.Operation, StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(kv.Key, "status", StringComparison.OrdinalIgnoreCase)) continue;
                            tags[kv.Key] = kv.Value;
                        }
                    }
                    finally { e.Dispose(); }
                }

                var se = ex as SilicaException;
                if (se != null)
                {
                    tags["error.code"] = se.Code ?? string.Empty;
                    tags["exception.id"] = se.ExceptionId.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }

            string level = "info";
            try
            {
                if (!string.IsNullOrEmpty(status))
                {
                    if (status.Equals("error", StringComparison.OrdinalIgnoreCase)) level = "error";
                    else if (status.Equals("warn", StringComparison.OrdinalIgnoreCase)) level = "warn";
                    else if (status.Equals("debug", StringComparison.OrdinalIgnoreCase)) level = "debug";
                    else level = "info";
                }
            }
            catch { level = "info"; }

            try
            {
                traces.Emit(componentName, operationName, level, tags, message ?? string.Empty, ex);
            }
            catch { }
        }

        internal static void EmitDebug(
            string component,
            string operation,
            string message,
            IReadOnlyDictionary<string, string>? more = null)
        {
            if (!EnableVerbose) return;
            Emit(component, operation, "debug", message, null, more);
        }
    }
}
