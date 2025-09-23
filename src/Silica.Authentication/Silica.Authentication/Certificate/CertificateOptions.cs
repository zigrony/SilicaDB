namespace Silica.Authentication.Certificate
{
    public sealed class CertificateOptions
    {
        public enum CertificateRevocationMode
        {
            None = 0,
            Online = 1,
            Offline = 2
        }

        // Validate the chain using available trust anchors (default true)
        public bool ValidateChain { get; init; } = true;

        // Require the certificate to be valid for client authentication EKU (1.3.6.1.5.5.7.3.2)
        public bool RequireClientAuthEku { get; init; } = true;

        // Allowed issuer distinguished names (optional). If empty, use platform trust.
        public string[]? AllowedIssuers { get; init; }

        // How to treat revocation checks
        public CertificateRevocationMode RevocationMode { get; init; } = CertificateRevocationMode.Online;

        // Clock skew tolerance in seconds when checking NotBefore/NotAfter
        public int ClockSkewSeconds { get; init; } = 60;

        // Optional filter for subject name matching (simple substring match)
        public string? SubjectNameMustContain { get; init; }

        // Optional small delay (milliseconds) on failures to reduce brute-force and timing signals.
        // 0 disables delay.
        public int FailureDelayMs { get; init; } = 0;
    }
}
