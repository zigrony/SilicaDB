using System;
using Silica.UI.Sessions;
using Silica.UI.Config;
using System.Threading.Tasks;
using Silica.Authentication.Local;
using Silica.Authentication.Abstractions;
using Silica.DiagnosticsCore.Metrics;
using Silica.UI.Diagnostics;
using Silica.UI.Metrics;

namespace Silica.UI.Auth
{
    /// <summary>
    /// Wraps Silica.Authentication providers.
    /// </summary>
    public class AuthManager
    {
        private readonly AuthConfig _config;
        private readonly SessionManagerAdapter _sessions;
        private readonly PasswordHasher _hasher;
        private readonly DbUserStore _userStore;
        private readonly LocalAuthenticationOptions _localOptions;
        private readonly LocalAuthenticator _local;
        private readonly IMetricsManager _metrics;

        public AuthManager(AuthConfig config, SessionManagerAdapter sessions)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            if (sessions is null) throw new ArgumentNullException(nameof(sessions));

            _config = config;
            _sessions = sessions;
            _hasher = new PasswordHasher();
            _metrics = new NullMetricsManager();
            UiMetrics.RegisterAll(_metrics, "Silica.UI.Auth");

            // Seed a local user (demo/dev only) from config.Auth
            var salt = PasswordHasher.CreateSalt();
            var seededUser = new LocalUser
            {
                Username = _config.SeedUsername,
                Salt = salt,
                PasswordHash = _hasher.Hash(_config.SeedPassword, salt),
                Roles = new[] { "User" }
            };

            _localOptions = new LocalAuthenticationOptions
            {
                MinPasswordLength = _config.MinPasswordLength,
                MaxFailedAttempts = _config.MaxFailedAttempts,
                HashIterations = _config.HashIterations,
                LockoutMinutes = _config.LockoutMinutes,
                FailureDelayMs = _config.FailureDelayMs
            };

            // In production, replace with DB fetch of users
            _userStore = new DbUserStore(new[] { seededUser });

            // Use the session provider from adapter; minimal metrics via NullMetricsManager
            _local = new LocalAuthenticator(
                _userStore,
                _hasher,
                _localOptions,
                metrics: new NullMetricsManager(),
                sessionProvider: _sessions.Provider);
        }

        public bool Authenticate(string username, string password)
        {
            var result = AuthenticateAsync(new AuthenticationContext
            {
                Username = username,
                Password = password
            }).GetAwaiter().GetResult();
            return result.Succeeded;
        }

        public Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context)
        {
            // metrics + trace: attempt
            UiMetrics.IncrementLoginAttempt(_metrics);
            UiDiagnostics.Emit("Silica.UI.Auth", "Authenticate", "start",
                "login_attempt", null,
                more: new Dictionary<string, string> { { "username", context.Username ?? string.Empty } });

            return _local.AuthenticateAsync(context).ContinueWith(t =>
            {
                var res = t.Result;
                if (!res.Succeeded)
                {
                    UiMetrics.IncrementLoginFailure(_metrics);
                    UiDiagnostics.Emit("Silica.UI.Auth", "Authenticate", "warn", "login_failed", null,
                        new Dictionary<string, string> { { "username", context.Username ?? string.Empty } });
                }
                else
                {
                    UiDiagnostics.Emit("Silica.UI.Auth", "Authenticate", "ok", "login_success", null,
                        new Dictionary<string, string> {
                            { "username", context.Username ?? string.Empty },
                            { "session_id", res.SessionId.ToString() }
                        });
                }
                return (IAuthenticationResult)res;
            });
        }
    }
}
