namespace Silica.Authentication.Jwt
{
    public sealed class JwtOptions
    {
        // Required for validation in most deployments
        public string? Issuer { get; init; }

        // Required audience (single value) — can be null to skip audience validation
        public string? Audience { get; init; }

        // Acceptable clock skew in seconds when validating expiry (default 60s)
        public int ClockSkewSeconds { get; init; } = 60;

        // Whether to require signature validation (default true)
        public bool RequireSignedTokens { get; init; } = true;

        // Optional: key material or reference. Validator implementation should accept keys via DI.
        public string? KeyId { get; init; }

        // Upper bound for token length to avoid memory pressure; 0 means no limit
        public int MaxTokenLength { get; init; } = 0;

        // Optional small delay (milliseconds) on failures to reduce brute-force and timing signals.
        // 0 disables delay. Keep values modest (e.g., 25–100ms) to avoid operational impact.
        public int FailureDelayMs { get; init; } = 0;

        // Test-only: allows magic string "EXPIRED" to simulate expiry in harness. Default off in enterprise.
        public bool EnableExpiredSentinel { get; init; } = false;
    }
}
