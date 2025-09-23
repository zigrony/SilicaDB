namespace Silica.Authentication.Ntlm
{
    public sealed class NtlmOptions
    {
        // Identify the service principal name expected (optional)
        public string? ServicePrincipal { get; init; }

        // Whether to allow Lm/NTLMv1 fallback (default: false)
        public bool AllowLegacyNtlm { get; init; } = false;

        // How to surface group/role mapping (in real world, hook to AD)
        public int RoleLookupTimeoutMs { get; init; } = 500;

        // Upper bound for token bytes length; 0 means no limit
        public int MaxTokenBytes { get; init; } = 0;

        // Optional small delay (milliseconds) on failures to reduce brute-force and timing signals.
        // 0 disables delay.
        public int FailureDelayMs { get; init; } = 0;
    }
}
