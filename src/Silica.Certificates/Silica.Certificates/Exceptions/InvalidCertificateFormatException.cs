// Filename: Silica.Certificates/Exceptions/InvalidCertificateFormatException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class InvalidCertificateFormatException : SilicaException
    {
        public string Format { get; }

        public InvalidCertificateFormatException(string format, string? detail = null)
            : base(
                code: CertificateExceptions.InvalidCertificateFormat.Code,
                message: $"Invalid certificate format '{format}'.{(string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)}",
                category: CertificateExceptions.InvalidCertificateFormat.Category,
                exceptionId: CertificateExceptions.Ids.InvalidCertificateFormat)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Format = format ?? string.Empty;
        }
    }
}
