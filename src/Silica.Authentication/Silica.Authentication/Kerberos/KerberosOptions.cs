namespace Silica.Authentication.Kerberos
{
    public sealed class KerberosOptions
    {
        // Service principal name expected by the service (e.g., HTTP/hostname@REALM)
        public string? ServicePrincipal { get; init; }

        // Allow forwardable tickets
        public bool RequireForwardable { get; init; } = false;

        // Timeout for any optional role lookup or directory probe (ms)
        public int RoleLookupTimeoutMs { get; init; } = 500;

        // Upper bound for token bytes length; 0 means no limit
        public int MaxTokenBytes { get; init; } = 0;

        // Optional small delay (milliseconds) on failures to reduce brute-force and timing signals.
        // 0 disables delay.
        public int FailureDelayMs { get; init; } = 0;
    }
}
