// Filename: Silica.Certificates/Exceptions/CertificateRenewalFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateRenewalFailedException : SilicaException
    {
        public string Provider { get; }
        public string Thumbprint { get; }

        public CertificateRenewalFailedException(string provider, string thumbprint, Exception? inner = null)
            : base(
                code: CertificateExceptions.RenewalFailed.Code,
                message: $"Certificate renewal failed (provider='{provider}', thumbprint='{thumbprint}').",
                category: CertificateExceptions.RenewalFailed.Category,
                exceptionId: CertificateExceptions.Ids.RenewalFailed,
                innerException: inner)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Provider = provider ?? string.Empty;
            Thumbprint = thumbprint ?? string.Empty;
        }
    }
}
