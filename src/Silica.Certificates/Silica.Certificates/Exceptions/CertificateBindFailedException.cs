// Filename: Silica.Certificates/Exceptions/CertificateBindFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.Certificates
{
    public sealed class CertificateBindFailedException : SilicaException
    {
        public string Endpoint { get; }
        public string Thumbprint { get; }

        public CertificateBindFailedException(string endpoint, string thumbprint, Exception? inner = null)
            : base(
                code: CertificateExceptions.BindFailed.Code,
                message: $"Certificate bind failed (endpoint='{endpoint}', thumbprint='{thumbprint}').",
                category: CertificateExceptions.BindFailed.Category,
                exceptionId: CertificateExceptions.Ids.BindFailed,
                innerException: inner)
        {
            CertificateExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Endpoint = endpoint ?? string.Empty;
            Thumbprint = thumbprint ?? string.Empty;
        }
    }
}
