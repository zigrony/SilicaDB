using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Local;
using Silica.UI.Diagnostics;

namespace Silica.UI.Auth
{
    /// <summary>
    /// Example database-backed user store.
    /// Replace the in-memory list with real DB queries.
    /// </summary>
    public class DbUserStore : ILocalUserStore
    {
        private readonly List<LocalUser> _users = new();

        public DbUserStore(IEnumerable<LocalUser> seedUsers)
        {
            _users.AddRange(seedUsers);
        }

        // Required by ILocalUserStore
        public LocalUser? FindByUsername(string username)
        {
            return _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        public Task<LocalUser?> FindByUsernameAsync(string username)
            => Task.FromResult(FindByUsername(username));

        public void RecordFailedAttempt(string username)
        {
            // Trace-only for now; replace with DB counters in production.
            UiDiagnostics.Emit("Silica.UI.Auth", "RecordFailedAttempt", "warn",
                "auth_failed_attempt", null,
                new Dictionary<string, string> { { "username", username ?? string.Empty } });
        }

        public void RecordSuccessfulLogin(string username)
        {
            // Trace-only for now; replace with DB updates in production.
            UiDiagnostics.Emit("Silica.UI.Auth", "RecordSuccessfulLogin", "ok",
                "auth_successful_login", null,
                new Dictionary<string, string> { { "username", username ?? string.Empty } });
        }


        public Task AddUserAsync(LocalUser user)
        {
            _users.Add(user);
            return Task.CompletedTask;
        }

        public Task UpdateUserAsync(LocalUser user)
        {
            var existing = _users.FindIndex(u => u.Username == user.Username);
            if (existing >= 0) _users[existing] = user;
            return Task.CompletedTask;
        }
    }
}
