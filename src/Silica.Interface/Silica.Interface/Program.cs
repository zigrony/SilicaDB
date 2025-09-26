using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Certificates.Providers;
using Silica.Certificates.Metrics;
using Silica.FrontEnds.Core;
using Silica.FrontEnds.Kestrel;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Local;
using Silica.Sessions.Contracts;
using Silica.Sessions.Implementation;

class Program
{
    static void Main(string[] args)
    {
        // 1) Diagnostics
        var options = new DiagnosticsOptions
        {
            DefaultComponent = "Silica.Interface",
            MinimumLevel = "info",
            EnableMetrics = true,
            EnableTracing = true,
            EnableLogging = true,
            DispatcherQueueCapacity = 1024,
            StrictMetrics = true,
            RequireTraceRedaction = true,
            MaxTagValueLength = 64,
            MaxTagsPerEvent = 10,
            SinkShutdownDrainTimeoutMs = 5000,
            GlobalTags = new Dictionary<string, string>
            {
                { TagKeys.Component, "Silica.Interface" },
                { TagKeys.Region, "dev" }
            },
            SensitiveTagKeys = new[] { "secret", "api_key", "password" }
        };
        options.Freeze();

        var instance = DiagnosticsCoreBootstrap.Start(options);
        CertificateMetrics.RegisterAll(instance.Metrics, "Silica.Certificates");

        // 2) Ephemeral cert
        var certProvider = new EphemeralCertificateProvider(metrics: instance.Metrics);
        var cert = certProvider.GetCertificate();
        if (OperatingSystem.IsWindows())
        {
            cert = new X509Certificate2(
                cert.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        }

        // 3) Builder
        var builder = WebApplication.CreateBuilder(args);

        // --- Local Authentication setup ---
        var salt = PasswordHasher.CreateSalt();
        var hasher = new PasswordHasher();
        var seededUser = new LocalUser
        {
            Username = "test",
            Salt = salt,
            PasswordHash = hasher.Hash("password", salt),
            Roles = new[] { "User" }
        };

        var localOptions = new LocalAuthenticationOptions
        {
            MinPasswordLength = 8,
            MaxFailedAttempts = 5,
            HashIterations = 310000,
            LockoutMinutes = 15,
            FailureDelayMs = 0
        };

        var userStore = new InMemoryLocalUserStore(new[] { seededUser }, localOptions);
        // Sessions provider (in-memory manager) with metrics bound to Interface component
        ISessionProvider sessionProvider = new SessionManager(instance.Metrics, "Silica.Interface.Sessions");

        // Pass session provider into authenticator (DI-like). Use named argument to avoid binding to principalMapper.
        var localAuthenticator = new LocalAuthenticator(
            userStore,
            hasher,
            localOptions,
            instance.Metrics,
            sessionProvider: sessionProvider);
        // -----------------------------------

        var registry = new FrontendRegistry(instance.Metrics);

        var kestrelOptions = new KestrelFrontendOptions
        {
            HttpPort = 5000,
            HttpsPort = 5001,
            StrictTlsCertificate = true,
            EnableVerboseDiagnostics = true,
            RootGetResponse = null, // disable hardcoded root response
            CertificateProvider = certProvider
        };

        var kestrelFrontend = new KestrelFrontend(kestrelOptions, instance.Metrics);
        registry.Add(kestrelFrontend);

        var app = registry.ConfigureAll(builder);

        // 4) Public static docs (pointing to Documentation folder)
        var publicPath = @"C:\GitHubRepos\SilicaDB\src\Silica.Documentation\";

        // Serve index.html / index.htm / default.html if present
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(publicPath),
            RequestPath = ""
        });

        // Serve static files
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(publicPath),
            RequestPath = ""
        });

        // Enable directory browsing so if no index file exists, a listing is shown
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = new PhysicalFileProvider(publicPath),
            RequestPath = ""
        });

        // 5) REST API root
        app.MapGet("/rest", () => Results.Ok(new { status = "ok", message = "REST API placeholder" }));

        // 6) Private management endpoints
        app.MapGet("/private", async (HttpContext ctx) =>
        {
            if (!ctx.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                return Results.Unauthorized();
            }

            var header = authHeader.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                return Results.Unauthorized();
            }

            string user, pass;
            try
            {
                var token = header.Substring("Basic ".Length).Trim();
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
                var parts = decoded.Split(':', 2);
                if (parts.Length != 2)
                {
                    ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                    return Results.Unauthorized();
                }
                user = parts[0];
                pass = parts[1];
            }
            catch
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                return Results.Unauthorized();
            }

            var result = await localAuthenticator.AuthenticateAsync(new AuthenticationContext
            {
                Username = user,
                Password = pass
            });

            if (!result.Succeeded)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Silica\"";
                return Results.Unauthorized();
            }

            // If a session was created by the authenticator, return it to client.
            if (result is { Succeeded: true, SessionId: Guid sid } && sid != Guid.Empty)
            {
                // Header + cookie for browser convenience
                ctx.Response.Headers["X-Silica-Session-Id"] = sid.ToString();
                ctx.Response.Cookies.Append("silica_session_id", sid.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromMinutes(15)
                });
            }

            return Results.Ok(new
            {
                principal = result.Principal,
                session_id = result.SessionId,
                roles = result.Roles
            });
        });

        // Session resume: read header/cookie, touch, and return a snapshot.
        app.MapGet("/private/resume", (HttpContext ctx) =>
        {
            string? sid = null;
            if (ctx.Request.Headers.TryGetValue("X-Silica-Session-Id", out var h) && h.Count > 0) sid = h[0];
            else if (ctx.Request.Cookies.TryGetValue("silica_session_id", out var c)) sid = c;

            if (string.IsNullOrWhiteSpace(sid) || !Guid.TryParse(sid, out var sessionGuid))
                return Results.BadRequest(new { error = "missing_or_invalid_session_id" });

            var session = sessionProvider.ResumeSession(sessionGuid);
            if (session == null) return Results.Unauthorized();

            // Activity touch; keep lifecycle accurate.
            session.Touch();
            var snap = session.ReadSnapshot();
            return Results.Ok(new
            {
                session_id = snap.SessionId,
                principal = snap.Principal,
                state = snap.State.ToString(),
                created_utc = snap.CreatedUtc,
                last_activity_utc = snap.LastActivityUtc
            });
        });

        // 7) Run
        registry.RunIfAnyFrontendOwnsLifecycle(app);

        // 8) Shutdown
        // No background sweeps started here; SessionManager can be managed centrally if needed.
        DiagnosticsCoreBootstrap.Stop(throwOnErrors: true);
    }
}
