using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Ntlm
{
    /// <summary>
    /// Abstract platform NTLM/SSPI operations. Keep this testable and small.
    /// Implementations will wrap SSPI on Windows or native NTLM libs on other platforms.
    /// </summary>
    public interface INtlmSecurityProvider
    {
        /// <summary>
        /// Validates an incoming NTLM negotiate/auth token and returns the authenticated principal name.
        /// Returns null if validation fails.
        /// </summary>
        /// <param name="token">Raw token bytes (e.g., from HTTP Negotiate header or AuthenticationContext.Token base64)</param>
        /// <param name="expectedService">Optional SPN to validate target service</param>
        /// <param name="cancellationToken">Cancellation</param>
        /// <returns>Authenticated principal (e.g., DOMAIN\User) or null for invalid</returns>
        Task<string?> ValidateTokenAsync(byte[] token, string? expectedService = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Indicates whether provider is available on this host (useful for runtime gating).
        /// </summary>
        bool IsAvailable { get; }
    }
}
