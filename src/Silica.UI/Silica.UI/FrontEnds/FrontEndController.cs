using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Silica.UI.Auth;
using Silica.UI.Sessions;
using Silica.UI.Config;
using Microsoft.AspNetCore.Http;
using Silica.Authentication.Abstractions;
using Silica.DiagnosticsCore.Metrics;
using System.Diagnostics;
using Silica.UI.Diagnostics;
using Silica.UI.Metrics;

namespace Silica.UI.FrontEnds
{
    /// <summary>
    /// Controls the lifecycle of the current frontend (Kestrel today).
    /// </summary>
    public class FrontEndController : IAsyncDisposable
    {
        private readonly FrontEndConfig _config;
        private readonly AuthManager _auth;
        private readonly SessionManagerAdapter _sessions;
        private IHost? _host;
        private readonly IMetricsManager _metrics;

        public FrontEndController(FrontEndConfig config, AuthManager auth, SessionManagerAdapter sessions)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

            _metrics = new NullMetricsManager();
            UiMetrics.RegisterAll(_metrics, "Silica.UI.FrontEnd");
        }
        public async ValueTask DisposeAsync()
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
                _host = null;
            }
        }
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_host != null)
                throw new InvalidOperationException("Frontend already running.");
            UiDiagnostics.Emit("Silica.UI.FrontEnd", "Start", "start",
                "frontend_starting", null, new Dictionary<string, string> { { "url", _config.Url } });

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    // Register your Silica services into DI
                    services.AddSingleton(_auth);      // never null due to ctor guard
                    services.AddSingleton(_sessions);  // never null due to ctor guard
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel()
                              .UseUrls(_config.Url)
                              .Configure(app =>
                              {
                                  var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
                                  UiDiagnostics.Emit("Silica.UI.FrontEnd", "Start", "info",
                                      "frontend_configured", null, new Dictionary<string, string> { { "environment", env.EnvironmentName } });

                                  if (env.IsDevelopment())
                                  {
                                      app.UseDeveloperExceptionPage();
                                  }

                                  // Simple test endpoint
                                  app.UseRouting();
                                  app.UseEndpoints(endpoints =>
                                  {
                                      endpoints.MapGet("/", async context =>
                                      {
                                          var sw = Stopwatch.StartNew();
                                          await context.Response.WriteAsync("Silica public page is running.");
                                          sw.Stop();
                                          UiMetrics.RecordPageLoadLatency(_metrics, sw.Elapsed.TotalMilliseconds);
                                          UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /", "ok",
                                              "public_root_served", null,
                                              new Dictionary<string, string> {
                                                  { "latency_ms", sw.Elapsed.TotalMilliseconds.ToString("F3") }
                                              });
                                      });

                                      // REST root (parity with original Program.cs)
                                      endpoints.MapGet("/rest", () =>
                                          Results.Ok(new { status = "ok", message = "REST API placeholder" }));

                                      endpoints.MapGet("/private", async context =>
                                      {
                                          if (!_config.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                          {
                                              // Still allow in dev; production should enforce TLS.
                                          }

                                          // Basic auth (if enabled)
                                          if (!_authMatchesConfigBasicEnabled())
                                          {
                                              context.Response.StatusCode = 501;
                                              await context.Response.WriteAsync("Basic auth disabled");
                                              return;
                                          }

                                          if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                                          {
                                              context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                                              UiMetrics.IncrementLoginFailure(_metrics);
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "warn",
                                                  "basic_auth_missing_header");

                                              context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                              return;
                                          }

                                          var header = authHeader.ToString();
                                          if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                                          {
                                              context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                                              UiMetrics.IncrementLoginFailure(_metrics);
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "warn",
                                                  "basic_auth_invalid_scheme");

                                              context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                              return;
                                          }

                                          string user = string.Empty, pass = string.Empty;
                                          try
                                          {
                                              var token = header.Substring("Basic ".Length).Trim();
                                              var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
                                              var parts = decoded.Split(':', 2);
                                              if (parts.Length != 2)
                                              {
                                                  context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                                                  context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                                  return;
                                              }
                                              user = parts[0];
                                              pass = parts[1];
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "info",
                                                  "basic_auth_header_parsed", null,
                                                  new Dictionary<string, string> { { "username", user ?? string.Empty } });
                                          }
                                          catch
                                          {
                                              context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                                              UiMetrics.IncrementLoginFailure(_metrics);
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "warn",
                                                  "basic_auth_failed", null,
                                                  new Dictionary<string, string> { { "username", user ?? string.Empty } });
                                              context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                              return;
                                          }

                                          var result = await _auth.AuthenticateAsync(new AuthenticationContext
                                          {
                                              Username = user,
                                              Password = pass
                                          });

                                          if (!result.Succeeded)
                                          {
                                              context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                                              context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                              return;
                                          }

                                          // Issue session id via header + secure cookie
                                          if (result is { Succeeded: true, SessionId: Guid sid } && sid != Guid.Empty)
                                          {
                                              context.Response.Headers["X-Silica-Session-Id"] = sid.ToString();
                                              context.Response.Cookies.Append("silica_session_id", sid.ToString(), new CookieOptions
                                              {
                                                  HttpOnly = true,
                                                  Secure = true,
                                                  SameSite = SameSiteMode.Strict,
                                                  MaxAge = TimeSpan.FromMinutes(15)
                                              });
                                          }
                                          UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "ok",
                                              "basic_auth_succeeded", null,
                                              new Dictionary<string, string> { { "username", user ?? string.Empty } });

                                          await context.Response.WriteAsJsonAsync(new
                                          {
                                              principal = result.Principal,
                                              session_id = result.SessionId,
                                              roles = result.Roles
                                          });
                                      });

                                      endpoints.MapGet("/private/resume", async context =>
                                      {
                                          string? sid = null;
                                          var sw = Stopwatch.StartNew();
                                          if (context.Request.Headers.TryGetValue("X-Silica-Session-Id", out var h) && h.Count > 0) sid = h[0];
                                          else if (context.Request.Cookies.TryGetValue("silica_session_id", out var c)) sid = c;

                                          if (string.IsNullOrWhiteSpace(sid) || !Guid.TryParse(sid, out var sessionGuid))
                                          {
                                              context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                              await context.Response.WriteAsJsonAsync(new { error = "missing_or_invalid_session_id" });
                                              return;
                                          }

                                          UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private/resume", "start",
                                              "session_resume_attempt", null,
                                              new Dictionary<string, string> { { "session_id", sessionGuid.ToString() } });

                                          dynamic? session = _sessions.Resume(sessionGuid);
                                          if (session is null)
                                          {
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private/resume", "warn",
                                                  "session_not_found", null, new Dictionary<string, string> { { "session_id", sessionGuid.ToString() } });
                                              context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                              return;
                                          }

                                          // Touch lifecycle and read snapshot
                                          session.Touch();
                                          var snap = session.ReadSnapshot();

                                          sw.Stop();
                                          UiMetrics.RecordSessionResumeLatency(_metrics, sw.Elapsed.TotalMilliseconds);
                                          UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private/resume", "ok",
                                              "session_resumed", null, new Dictionary<string, string> { { "latency_ms", sw.Elapsed.TotalMilliseconds.ToString("F3") } });
                                          await context.Response.WriteAsJsonAsync(new
                                          {
                                              session_id = snap.SessionId,
                                              principal = snap.Principal,
                                              state = snap.State.ToString(),
                                              created_utc = snap.CreatedUtc,
                                              last_activity_utc = snap.LastActivityUtc
                                          });
                                      });

                                  });
                              });
                })
                .Build();

            await _host.StartAsync(cancellationToken);
            UiDiagnostics.Emit("Silica.UI.FrontEnd", "Start", "ok",
                "frontend_started", null, new Dictionary<string, string> { { "url", _config.Url } });
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_host == null) return;

            UiDiagnostics.Emit("Silica.UI.FrontEnd", "Stop", "start", "frontend_stopping");
            await _host.StopAsync(cancellationToken);
            _host.Dispose();
            _host = null;
            UiDiagnostics.Emit("Silica.UI.FrontEnd", "Stop", "ok", "frontend_stopped");
        }

        public async Task RestartAsync(CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken);
            await StartAsync(cancellationToken);
        }

        public void Dispose()
        {
            _host?.Dispose();
        }

        private bool _authMatchesConfigBasicEnabled() => true; // placeholder for future provider switching
    }
}
