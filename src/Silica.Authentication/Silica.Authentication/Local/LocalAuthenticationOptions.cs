namespace Silica.Authentication.Local
{
    /// <summary>
    /// Configurable options for local authentication.
    /// </summary>
    public sealed class LocalAuthenticationOptions
    {
        public int MinPasswordLength { get; init; } = 8;
        public int MaxFailedAttempts { get; init; } = 5;
        public int HashIterations { get; init; } = 310000;
        // Lockout duration in minutes for in-memory store auto-unlock
        public int LockoutMinutes { get; init; } = 15;

        // Optional small delay (milliseconds) on failures to reduce brute-force and timing signals.
        // 0 disables delay. Keep values modest (e.g., 25–100ms) to avoid operational impact.
        public int FailureDelayMs { get; init; } = 0;
    }
}
