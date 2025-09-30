using System;
using Silica.UI.Sessions;
using Silica.UI.Config;
using System.Threading.Tasks;
using Silica.Authentication.Local;
using Silica.Authentication.Abstractions;
using Silica.DiagnosticsCore.Metrics;
using Silica.UI.Diagnostics;
using Silica.UI.Metrics;
using System.Collections.Generic;

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
        public bool IsBasicAuthEnabled => _config.EnableBasicAuth;

        public AuthManager(AuthConfig config, SessionManagerAdapter sessions)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            if (sessions is null) throw new ArgumentNullException(nameof(sessions));

            _config = config;
            _sessions = sessions;
            _hasher = new PasswordHasher();
            // Single metrics manager instance per component
            _metrics = new NullMetricsManager();
            UiMetrics.RegisterAll(_metrics, "Silica.UI.Auth");

            // Seed a local user (demo/dev only), gated by config and non-empty values
            var seedUsers = new List<LocalUser>();
            if (!string.IsNullOrWhiteSpace(_config.SeedUsername) &&
                !string.IsNullOrWhiteSpace(_config.SeedPassword) &&
                string.Equals(_config.Provider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var salt = PasswordHasher.CreateSalt();
                var seededUser = new LocalUser
                {
                    Username = _config.SeedUsername,
                    Salt = salt,
                    PasswordHash = _hasher.Hash(_config.SeedPassword, salt),
                    Roles = new[] { "User" }
                };
                seedUsers.Add(seededUser);
            }
            _localOptions = new LocalAuthenticationOptions
            {
                MinPasswordLength = _config.MinPasswordLength,
                MaxFailedAttempts = _config.MaxFailedAttempts,
                HashIterations = _config.HashIterations,
                LockoutMinutes = _config.LockoutMinutes,
                FailureDelayMs = _config.FailureDelayMs
            };

            // In production, replace with DB fetch of users
            _userStore = new DbUserStore(seedUsers);

            // Use the session provider from adapter; minimal metrics via NullMetricsManager
            _local = new LocalAuthenticator(
                _userStore,
                _hasher,
                _localOptions,
                metrics: _metrics);
        }

        public bool Authenticate(string username, string password)
        {
            // Synchronous convenience; prefer async in server code paths.
            var ctx = new AuthenticationContext { Username = username, Password = password };
            var result = AuthenticateAsync(ctx).GetAwaiter().GetResult();
            return result.Succeeded;
        }

        public async Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            // metrics + trace: attempt
            UiMetrics.IncrementLoginAttempt(_metrics);
            UiDiagnostics.Emit("Silica.UI.Auth", "Authenticate", "start",
                "login_attempt", null,
                more: new Dictionary<string, string> { { "username", context.Username ?? string.Empty } });

            var res = await _local.AuthenticateAsync(context);
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
                    });
            }
            return res;
        }
    }
}
