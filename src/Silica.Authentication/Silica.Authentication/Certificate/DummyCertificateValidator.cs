using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Certificate
{
    /// <summary>
    /// Deterministic test validator.
    /// - Certificate subject containing "CN=VALID" -> valid principal "cert-user"
    /// - Subject containing "CN=EXPIRED" -> invalid with FailureReason "expired"
    /// - Subject containing "CN=BADCHAIN" -> invalid with FailureReason "bad_chain"
    /// - Otherwise invalid
    /// </summary>
    public sealed class DummyCertificateValidator : ICertificateValidator
    {
        public bool IsAvailable => true;

        public Task<CertificateValidationResult> ValidateAsync(X509Certificate2 cert, CertificateOptions options, CancellationToken cancellationToken = default)
        {
            if (cert == null) return Task.FromResult(new CertificateValidationResult { IsValid = false, FailureReason = "null_certificate" });

            var subj = cert.Subject ?? string.Empty;

            if (subj.Contains("CN=VALID", System.StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CertificateValidationResult { IsValid = true, Principal = "cert-user" });
            }

            if (subj.Contains("CN=EXPIRED", System.StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CertificateValidationResult { IsValid = false, FailureReason = "expired" });
            }

            if (subj.Contains("CN=BADCHAIN", System.StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CertificateValidationResult { IsValid = false, FailureReason = "bad_chain" });
            }

            return Task.FromResult(new CertificateValidationResult { IsValid = false, FailureReason = "unknown" });
        }
    }
}
