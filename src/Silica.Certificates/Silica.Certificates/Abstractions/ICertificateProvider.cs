using System.Security.Cryptography.X509Certificates;

namespace Silica.Certificates.Abstractions
{
    /// <summary>
    /// Provides an X509 certificate for use in TLS endpoints (e.g., Kestrel).
    /// </summary>
    public interface ICertificateProvider
    {
        /// <summary>
        /// Returns a certificate instance for use in HTTPS/TLS. Implementations should ensure
        /// consumer disposal does not impact provider integrity (e.g., by returning a fresh instance).
        /// </summary>
        X509Certificate2 GetCertificate();
    }
}
