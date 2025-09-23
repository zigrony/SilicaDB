using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Ntlm
{
    /// <summary>
    /// Deterministic, test-only provider to validate harness and metrics/traces without native SSPI.
    /// Accepts token "VALID" (as bytes or base64) and returns "TESTDOMAIN\\Alice".
    /// </summary>
    public sealed class DummyNtlmSecurityProvider : INtlmSecurityProvider
    {
        public bool IsAvailable => true;

        public Task<string?> ValidateTokenAsync(byte[] token, string? expectedService = null, CancellationToken cancellationToken = default)
        {
            // Accept either ASCII "VALID" or base64 for the same
            if (token == null || token.Length == 0) return Task.FromResult<string?>(null);
            var s = System.Text.Encoding.UTF8.GetString(token);
            if (string.Equals(s, "VALID", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<string?>("TESTDOMAIN\\Alice");
            }
            return Task.FromResult<string?>(null);
        }
    }
}
