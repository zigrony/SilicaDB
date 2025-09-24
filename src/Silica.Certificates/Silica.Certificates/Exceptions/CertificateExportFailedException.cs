// Filename: Silica.Certificates/Exceptions/CertificateExportFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateExportFailedException : SilicaException
    {
        public string Format { get; }
        public string Thumbprint { get; }

        public CertificateExportFailedException(string format, string thumbprint, Exception? inner = null)
            : base(
                code: CertificateExceptions.ExportFailed.Code,
                message: $"Certificate export failed (format='{format}', thumbprint='{thumbprint}').",
                category: CertificateExceptions.ExportFailed.Category,
                exceptionId: CertificateExceptions.Ids.ExportFailed,
                innerException: inner)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Format = format ?? string.Empty;
            Thumbprint = thumbprint ?? string.Empty;
        }
    }
}
