using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Jwt
{
    /// <summary>
    /// Abstracts JWT parsing and validation so authenticators remain testable.
    /// Implementations validate signature, issuer, audience, expiry, and return claims.
    /// Returns null if token is invalid.
    /// </summary>
    public interface IJwtValidator
    {
        /// <summary>
        /// Validate an encoded JWT and returns a dictionary of claims if valid, otherwise null.
        /// </summary>
        Task<IDictionary<string, string>?> ValidateAsync(string token, JwtOptions options, CancellationToken cancellationToken = default);
    }
}
