// Filename: Silica.Certificates/Exceptions/CertificateGenerationFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateGenerationFailedException : SilicaException
    {
        public string Provider { get; }
        public string Subject { get; }

        public CertificateGenerationFailedException(string provider, string subject, Exception? inner = null)
            : base(
                code: CertificateExceptions.GenerationFailed.Code,
                message: $"Certificate generation failed (provider='{provider}', subject='{subject}').",
                category: CertificateExceptions.GenerationFailed.Category,
                exceptionId: CertificateExceptions.Ids.GenerationFailed,
                innerException: inner)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Provider = provider ?? string.Empty;
            Subject = subject ?? string.Empty;
        }
    }
}
