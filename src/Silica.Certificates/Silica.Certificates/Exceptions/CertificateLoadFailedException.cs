// Filename: Silica.Certificates/Exceptions/CertificateLoadFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateLoadFailedException : SilicaException
    {
        public string Source { get; }
        public string PathOrKey { get; }

        public CertificateLoadFailedException(string source, string pathOrKey, Exception? inner = null)
            : base(
                code: CertificateExceptions.LoadFailed.Code,
                message: $"Certificate load failed (source='{source}', key='{pathOrKey}').",
                category: CertificateExceptions.LoadFailed.Category,
                exceptionId: CertificateExceptions.Ids.LoadFailed,
                innerException: inner)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Source = source ?? string.Empty;
            PathOrKey = pathOrKey ?? string.Empty;
        }
    }
}
