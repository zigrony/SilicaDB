// Filename: Silica.Certificates/Exceptions/CertificateExpiredException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateExpiredException : SilicaException
    {
        public DateTimeOffset NotBefore { get; }
        public DateTimeOffset NotAfter { get; }
        public string Thumbprint { get; }

        public CertificateExpiredException(string thumbprint, DateTimeOffset notBefore, DateTimeOffset notAfter)
            : base(
                code: CertificateExceptions.CertificateExpired.Code,
                message: $"Certificate validity window expired (thumbprint='{thumbprint}', NotBefore={notBefore:u}, NotAfter={notAfter:u}).",
                category: CertificateExceptions.CertificateExpired.Category,
                exceptionId: CertificateExceptions.Ids.CertificateExpired)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Thumbprint = thumbprint ?? string.Empty;
            NotBefore = notBefore;
            NotAfter = notAfter;
        }
    }
}
