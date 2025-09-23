namespace Silica.Authentication.Local
{
    /// <summary>
    /// Contract for retrieving local user records.
    /// Keeps persistence abstract (DB, file, in-memory).
    /// </summary>
    public interface ILocalUserStore
    {
        LocalUser? FindByUsername(string username);
        void RecordFailedAttempt(string username);
        void RecordSuccessfulLogin(string username);
    }

    /// <summary>
    /// Simple DTO for user data.
    /// </summary>
    public sealed class LocalUser
    {
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public byte[] Salt { get; init; } = System.Array.Empty<byte>();
        public string[] Roles { get; init; } = Array.Empty<string>();
        public bool IsLocked { get; init; }
    }
}
