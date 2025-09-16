using System;
using System.Collections.Generic;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;

namespace Silica.Storage.Encryption.Diagnostics
{
    internal static class EncryptionDiagnostics
    {
        private const string Component = "Silica.Storage.Encryption";
        // Gate per-frame info/debug logs in hot paths. Defaults to false (quiet).
        // Can be toggled by hosting process during diagnostics sessions.
        public static volatile bool EnableFrameInfoLogs = false;

        internal static void Emit(string operation, string level, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null)
        {
            // No-op if diagnostics are not started to avoid bootstrap-time crashes.
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

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                tags[TagKeys.Component] = Component;
                tags[TagKeys.Operation] = operation;
                if (more is not null)
                {
                    foreach (var kv in more) tags[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // If tag population fails, still attempt emit with what we have.
            }
            try
            {
                traces.Emit(Component, operation, level, tags, message, ex);
            }
            catch
            {
                // Swallow all diagnostics faults.
            }
        }
    }
}
