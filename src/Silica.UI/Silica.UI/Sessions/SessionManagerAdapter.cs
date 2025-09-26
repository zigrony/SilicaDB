using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Sessions.Contracts;
using Silica.Sessions.Implementation;
using Silica.UI.Config;
using Silica.UI.Diagnostics;
using Silica.UI.Metrics;

namespace Silica.UI.Sessions
{
    /// <summary>
    /// Wraps Silica.Sessions for lifecycle management.
    /// </summary>
    public class SessionManagerAdapter : IDisposable
    {
        private readonly ISessionProvider _provider;
        private readonly IMetricsManager _metrics;
        private readonly SessionConfig _config;

        public SessionManagerAdapter(SessionConfig config)
        {
            _config = config ?? new SessionConfig();
            _metrics = new NullMetricsManager();
            UiMetrics.RegisterAll(_metrics, "Silica.UI.Sessions");
            _provider = new SessionManager(_metrics, "Silica.UI.Sessions");
        }

        public void Initialize()
        {
            UiDiagnostics.Emit("Silica.UI.Sessions", "Initialize", "start", "session_manager_init");
        }

        public void Dispose()
        {
            UiDiagnostics.Emit("Silica.UI.Sessions", "Dispose", "ok", "session_manager_disposed");
        }

        public ISessionProvider Provider => _provider;

        public ISessionContext? Resume(Guid sessionId)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var s = _provider.ResumeSession(sessionId);
                UiMetrics.RecordSessionResumeLatency(_metrics, sw.Elapsed.TotalMilliseconds);

                if (s is null)
                {
                    UiDiagnostics.Emit("Silica.UI.Sessions", "Resume", "warn", "session_resume_failed", null,
                        new Dictionary<string, string> { { "session_id", sessionId.ToString() } });
                }
                else
                {
                    UiDiagnostics.Emit("Silica.UI.Sessions", "Resume", "ok", "session_resumed", null,
                        new Dictionary<string, string> { { "session_id", sessionId.ToString() } });
                }

                return s;
            }
            finally
            {
                sw.Stop();
            }
        }

        public int IdleTimeoutMinutes => _config.IdleTimeoutMinutes;
    }

    // Minimal metrics manager to satisfy constructors without bootstrapping DiagnosticsCore.
    internal sealed class NullMetricsManager : IMetricsManager
    {
        public void Register(MetricDefinition def) { }
        public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
        public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
        public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
    }
}
