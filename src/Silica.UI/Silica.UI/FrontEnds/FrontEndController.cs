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
using System.Collections.Generic;
using Microsoft.Extensions.FileProviders;
using Silica.DiagnosticsCore;
using Silica.Sessions.Contracts;

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
            // Ensure UI exception catalog is registered before any failure paths.
            Silica.UI.Exceptions.UIExceptions.RegisterAll();
            if (_host != null)
                throw new Silica.UI.Exceptions.ComponentRenderFailureException(
                    componentId: "Silica.UI.FrontEndController",
                    inner: null);
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

                                  app.Use(async (context, next) =>
                                  {
                                      // Try header then cookie
                                      string? sid = null;
                                      if (context.Request.Headers.TryGetValue("X-Silica-Session-Id", out var h) && h.Count > 0)
                                          sid = h[0];
                                      else if (context.Request.Cookies.TryGetValue("silica_session_id", out var c))
                                          sid = c;

                                      ISessionContext? session = null;
                                      if (!string.IsNullOrWhiteSpace(sid) && Guid.TryParse(sid, out var sessionGuid))
                                      {
                                          session = _sessions.Resume(sessionGuid); // SessionManagerAdapter.Resume returns ISessionContext?
                                      }

                                      if (session == null)
                                      {
                                          // Create anonymous session (no principal)
                                          session = _sessions.Provider.CreateSession(
                                              principal: null,
                                              idleTimeout: TimeSpan.FromMinutes(_sessions.IdleTimeoutMinutes),
                                              transactionTimeout: TimeSpan.FromMinutes(5),
                                              coordinatorNode: "frontend");

                                          // Persist for client
                                          var cookieOptions = new CookieOptions
                                          {
                                              HttpOnly = true,
                                              Secure = true,
                                              SameSite = SameSiteMode.Strict,
                                              MaxAge = TimeSpan.FromMinutes(_sessions.IdleTimeoutMinutes)
                                          };
                                          context.Response.Cookies.Append("silica_session_id", session.SessionId.ToString(), cookieOptions);
                                          context.Response.Headers["X-Silica-Session-Id"] = session.SessionId.ToString();
                                      }
                                      else
                                      {
                                          // touch resumed session to update last-activity
                                          try { session.Touch(); } catch { }
                                      }

                                      // Make session available to downstream code
                                      context.Items["SilicaSession"] = session;

                                      await next();
                                  });

                                  app.Use(async (context, next) =>
                                  {
                                      UiDiagnostics.Emit("Silica.UI.FrontEnd", "Request", "info",
                                          $"Request {context.Request.Method} {context.Request.Path}",
                                          null,
                                          new Dictionary<string, string> {
                                            { "method", context.Request.Method },
                                            { "path", context.Request.Path }
                                          });
                                      await next();
                                  });

                                  app.Use(async (context, next) =>
                                  {
                                      var sw = Stopwatch.StartNew();
                                      await next(); // Let FileServer handle the actual file
                                      sw.Stop();

                                      UiMetrics.RecordPageLoadLatency(_metrics, sw.Elapsed.TotalMilliseconds);

                                      UiDiagnostics.Emit("Silica.UI.FrontEnd", "Request", "info",
                                          "resource_served", null,
                                          new Dictionary<string, string> {
                                            { "method", context.Request.Method },
                                            { "path", context.Request.Path },
                                            { "latency_ms", sw.Elapsed.TotalMilliseconds.ToString("F3") },
                                            { "status_code", context.Response.StatusCode.ToString() }
                                          });
                                  });

                                  app.UseFileServer(new FileServerOptions
                                  {
                                      FileProvider = new PhysicalFileProvider(@"C:\GitHubRepos\SilicaDB\Documentation"),
                                      RequestPath = "",
                                      EnableDefaultFiles = true
                                  });
                                  app.UseEndpoints(endpoints =>
                                  {
                                      endpoints.MapGet("/api/logs", () =>
                                      {
                                          var sink = DiagnosticsCoreBootstrap.Instance.BoundedSink;
                                          return Results.Json(sink.GetSnapshot());
                                      });
                                      endpoints.MapGet("/api/metrics", () =>
                                      {
                                          var sink = DiagnosticsCoreBootstrap.Instance.BoundedMetricsSink;
                                          if (sink == null)
                                          {
                                              return Results.Json(new { error = "metrics_sink_not_available" });
                                          }

                                          var snapshot = sink.GetSnapshot();
                                          return Results.Json(snapshot);
                                      });


                                      // REST root (parity with original Program.cs)
                                      endpoints.MapGet("/rest", () =>
                                          Results.Ok(new { status = "ok", message = "REST API placeholder" }));

                                      // REST root (parity with original Program.cs)
                                      endpoints.MapGet("/rest", () =>
                                          Results.Ok(new { status = "ok", message = "REST API placeholder" }));

                                      endpoints.MapGet("/private", async context =>
                                      {
                                          // Enforce HTTPS for private endpoints if configured
                                          if (_config.EnforceHttpsForPrivate &&
                                              !context.Request.IsHttps)
                                          {
                                              UiDiagnostics.Emit("Silica.UI.FrontEnd", "GET /private", "warn",
                                                  "tls_required");
                                              context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                              await context.Response.WriteAsync("TLS required");
                                              return;
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
                                                  MaxAge = TimeSpan.FromMinutes(_sessions.IdleTimeoutMinutes)
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

                                          var session = _sessions.Resume(sessionGuid);
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

        private bool _authMatchesConfigBasicEnabled()
            => _auth.IsBasicAuthEnabled;
    }
}
