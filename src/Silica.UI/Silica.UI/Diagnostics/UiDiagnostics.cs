// Filename: Silica.UI/Diagnostics/UiDiagnostics.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Exceptions;

namespace Silica.UI.Diagnostics
{
    internal static class UiDiagnostics
    {
        public static volatile bool EnableVerbose = false;

        static UiDiagnostics()
        {
            try { ReloadFromEnvironment(); } catch { }
        }

        internal static void SetVerbose(bool on) => EnableVerbose = on;

        internal static void ReloadFromEnvironment()
        {
            try
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_UI_VERBOSE"); } catch { }
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
            }
            catch { }
        }

        internal static void Emit(string component, string operation, string level, string message,
            Exception? ex = null, IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; }
            catch { return; }

            var tags = new Dictionary<string, string>(more is null ? 2 : (2 + more.Count), StringComparer.OrdinalIgnoreCase);
            string componentName = "Silica.UI";
            string operationName = operation ?? string.Empty;
            try
            {
                componentName = string.IsNullOrWhiteSpace(component) ? "Silica.UI" : component;
                tags[TagKeys.Component] = componentName;
                tags[TagKeys.Operation] = operationName;

                if (more is not null)
                {
                    foreach (var kv in more)
                    {
                        if (string.Equals(kv.Key, TagKeys.Component, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(kv.Key, TagKeys.Operation, StringComparison.OrdinalIgnoreCase)) continue;
                        tags[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }

            try
            {
                var lvl = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
                if (lvl == "fatal") lvl = "error";
                if (lvl == "trace") lvl = "debug";

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

        internal static void EmitDebug(string component, string operation, string message,
            Exception? ex = null, IReadOnlyDictionary<string, string>? more = null, bool allowDebug = false)
        {
            if (!EnableVerbose && !allowDebug) return;
            Emit(component, operation, "debug", message, ex, more);
        }
    }
}
