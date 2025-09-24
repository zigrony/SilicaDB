// Filename: Silica.Certificates/Exceptions/CertificateNotFoundException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateNotFoundException : SilicaException
    {
        public string Source { get; }
        public string PathOrKey { get; }

        public CertificateNotFoundException(string source, string pathOrKey)
            : base(
                code: CertificateExceptions.CertificateNotFound.Code,
                message: $"Certificate not found (source='{source}', key='{pathOrKey}').",
                category: CertificateExceptions.CertificateNotFound.Category,
                exceptionId: CertificateExceptions.Ids.CertificateNotFound)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Source = source ?? string.Empty;
            PathOrKey = pathOrKey ?? string.Empty;
        }
    }
}
