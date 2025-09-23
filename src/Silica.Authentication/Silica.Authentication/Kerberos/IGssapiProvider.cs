using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Kerberos
{
    /// <summary>
    /// Minimal GSSAPI/SSPI provider abstraction for Kerberos token validation.
    /// Implementations wrap platform-specific libraries and return the authenticated principal name.
    /// </summary>
    public interface IGssapiProvider
    {
        /// <summary>
        /// Validates the received SPNEGO/Kerberos token and returns the principal (e.g., user@REALM) on success.
        /// Returns null on validation failure.
        /// </summary>
        Task<string?> ValidateTokenAsync(byte[] token, string? expectedService, CancellationToken cancellationToken = default);

        /// <summary>
        /// True when provider is available on this host (native libs, configs present).
        /// </summary>
        bool IsAvailable { get; }
    }
}
