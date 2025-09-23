using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Kerberos
{
    /// <summary>
    /// Test-only provider. Accepts token "VALID" (ASCII) and returns "user@REALM".
    /// </summary>
    public sealed class DummyGssapiProvider : IGssapiProvider
    {
        public bool IsAvailable => true;

        public Task<string?> ValidateTokenAsync(byte[] token, string? expectedService, CancellationToken cancellationToken = default)
        {
            if (token == null || token.Length == 0) return Task.FromResult<string?>(null);
            var s = System.Text.Encoding.UTF8.GetString(token);
            if (string.Equals(s, "VALID", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<string?>("user@REALM");
            }
            return Task.FromResult<string?>(null);
        }
    }
}
