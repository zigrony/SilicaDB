using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Jwt
{
    /// <summary>
    /// Deterministic test validator:
    /// - Accepts token "VALID" → returns claims { "sub": "testuser", "role": "User" }
    /// - Accepts token "EXPIRED" → returns null to simulate expiry
    /// - All other tokens → null
    /// </summary>
    public sealed class DummyJwtValidator : IJwtValidator
    {
        public Task<IDictionary<string, string>?> ValidateAsync(string token, JwtOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(token)) return Task.FromResult<IDictionary<string, string>?>(null);

            if (string.Equals(token, "VALID", StringComparison.OrdinalIgnoreCase))
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sub"] = "testuser",
                    ["role"] = "User"
                };
                return Task.FromResult<IDictionary<string, string>?>(dict);
            }

            // Simulate expired or invalid tokens as null
            return Task.FromResult<IDictionary<string, string>?>(null);
        }
    }
}
