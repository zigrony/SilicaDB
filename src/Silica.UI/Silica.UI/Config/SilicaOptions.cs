namespace Silica.UI.Config
{
    public class SilicaOptions
    {
        public AuthConfig AuthConfig { get; set; } = new AuthConfig();
        public SessionConfig SessionConfig { get; set; } = new SessionConfig();
        public FrontEndConfig FrontEndConfig { get; set; } = new FrontEndConfig();
    }

    public class AuthConfig
    {
        public string Provider { get; set; } = "Local";
        public bool EnableBasicAuth { get; set; } = true;
        public string SeedUsername { get; set; } = "test";
        public string SeedPassword { get; set; } = "password";
        public int MinPasswordLength { get; set; } = 8;
        public int MaxFailedAttempts { get; set; } = 5;
        public int HashIterations { get; set; } = 310000;
        public int LockoutMinutes { get; set; } = 15;
        public int FailureDelayMs { get; set; } = 0;
        public string? ConnectionString { get; set; }
    }

    public class SessionConfig
    {
        public int IdleTimeoutMinutes { get; set; } = 30;
    }

    public class FrontEndConfig
    {
        public string Url { get; set; } = "https://localhost:5001";
    }
}
