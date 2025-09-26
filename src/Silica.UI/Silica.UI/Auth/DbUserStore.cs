using System;
using System.Collections.Generic;
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
            if (seedUsers != null)
            {
                foreach (var u in seedUsers)
                {
                    if (u == null) continue;
                    // Avoid duplicate seed entries by username (case-insensitive)
                    var existingIndex = FindIndexByUsername(u.Username);
                    if (existingIndex >= 0) _users[existingIndex] = u;
                    else _users.Add(u);
                }
            }
        }

        // Required by ILocalUserStore
        public LocalUser? FindByUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            var idx = FindIndexByUsername(username);
            if (idx < 0) return null;
            return _users[idx];
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
        private int FindIndexByUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return -1;
            for (int i = 0; i < _users.Count; i++)
            {
                var u = _users[i];
                if (u != null && string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        public Task AddUserAsync(LocalUser user)
        {
            if (user == null) return Task.CompletedTask;
            var idx = FindIndexByUsername(user.Username);
            if (idx >= 0) _users[idx] = user;
            else _users.Add(user);
            return Task.CompletedTask;
        }

        public Task UpdateUserAsync(LocalUser user)
        {
            if (user == null) return Task.CompletedTask;
            var existing = FindIndexByUsername(user.Username);
            if (existing >= 0) _users[existing] = user;
            return Task.CompletedTask;
        }
    }
}
