using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Authentication.Certificate
{
    public sealed class CertificateValidationResult
    {
        public bool IsValid { get; init; }
        public string? Principal { get; init; }
        public string? FailureReason { get; init; }
    }

    public interface ICertificateValidator
    {
        /// <summary>
        /// Validate the given certificate and return a CertificateValidationResult.
        /// </summary>
        Task<CertificateValidationResult> ValidateAsync(X509Certificate2 cert, CertificateOptions options, CancellationToken cancellationToken = default);

        /// <summary>
        /// Indicates whether this validator is available on this host.
        /// </summary>
        bool IsAvailable { get; }
    }
}
