namespace Silica.Authentication.Abstractions
{
    /// <summary>
    /// Encapsulates the input to an authentication attempt.
    /// Different authenticators will interpret fields differently.
    /// </summary>
    public sealed class AuthenticationContext
    {
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? Token { get; init; }
        /// <summary>
        /// Raw token bytes when a binary credential is provided (e.g., SPNEGO/NTLM).
        /// Prefer this over base64 in Token when available.
        /// </summary>
        public byte[]? TokenBytes { get; init; }

        public byte[]? CertificateBytes { get; init; }

        /// <summary>
        /// Arbitrary metadata (e.g., client IP, connection ID).
        /// </summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }
}
