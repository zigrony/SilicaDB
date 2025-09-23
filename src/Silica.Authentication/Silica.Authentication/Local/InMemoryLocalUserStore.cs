using System.Collections.Concurrent;

namespace Silica.Authentication.Local
{
    /// <summary>
    /// In-memory implementation of ILocalUserStore for testing/demo.
    /// </summary>
    public sealed class InMemoryLocalUserStore : ILocalUserStore
    {
        private readonly ConcurrentDictionary<string, LocalUser> _users = new();
        private readonly ConcurrentDictionary<string, int> _failCounts = new();
        private readonly ConcurrentDictionary<string, DateTime> _lockouts = new();
        private readonly TimeSpan _lockoutDuration;
        private readonly int _maxFailedAttempts;

        public InMemoryLocalUserStore(IEnumerable<LocalUser> seedUsers, int maxFailedAttempts = 5, TimeSpan? lockoutDuration = null)
        {
            _maxFailedAttempts = maxFailedAttempts <= 0 ? 5 : maxFailedAttempts;
            _lockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(15);
            foreach (var user in seedUsers)
                _users[user.Username] = user;
        }

        // Overload to construct from LocalAuthenticationOptions for policy alignment.
        public InMemoryLocalUserStore(IEnumerable<LocalUser> seedUsers, LocalAuthenticationOptions options)
        {
            int maxAttempts = (options == null || options.MaxFailedAttempts <= 0) ? 5 : options.MaxFailedAttempts;
            _maxFailedAttempts = maxAttempts;
            int minutes = (options == null || options.LockoutMinutes <= 0) ? 15 : options.LockoutMinutes;
            _lockoutDuration = TimeSpan.FromMinutes(minutes);
            foreach (var user in seedUsers)
                _users[user.Username] = user;
        }

        public LocalUser? FindByUsername(string username)
        {
            if (_users.TryGetValue(username, out var user))
            {
                if (user.IsLocked && _lockouts.TryGetValue(username, out var lockedAt))
                {
                    if (DateTime.UtcNow - lockedAt > _lockoutDuration)
                    {
                        // auto-unlock
                        var unlocked = new LocalUser
                        {
                            Username = user.Username,
                            PasswordHash = user.PasswordHash,
                            Salt = user.Salt,
                            Roles = user.Roles,
                            IsLocked = false
                        };
                        _users[username] = unlocked;
                        _lockouts.TryRemove(username, out _);
                        // Reset failure count upon auto-unlock to avoid immediate relock-on-next-failure
                        // and restore a clean authentication state.
                        _failCounts.TryRemove(username, out _);
                        return unlocked;
                    }
                }
                return user;
            }
            return null;
        }

        public void RecordFailedAttempt(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            var newCount = _failCounts.AddOrUpdate(username, 1, static (_, current) => current + 1);
            if (newCount >= _maxFailedAttempts)
            {
                // Lock account by replacing the record immutably
                if (_users.TryGetValue(username, out var existing))
                {
                    var locked = new LocalUser
                    {
                        Username = existing.Username,
                        PasswordHash = existing.PasswordHash,
                        Salt = existing.Salt,
                        Roles = existing.Roles,
                        IsLocked = true
                    };
                    _users[username] = locked;
                    _lockouts[username] = DateTime.UtcNow;
                }
            }
        }

        public void RecordSuccessfulLogin(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            _failCounts.TryRemove(username, out _);
        }
    }
}
