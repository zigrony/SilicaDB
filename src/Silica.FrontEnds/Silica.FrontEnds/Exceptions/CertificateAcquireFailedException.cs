// File: Silica.FrontEnds/Exceptions/CertificateAcquireFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.FrontEnds.Exceptions
{
    /// <summary>
    /// Thrown when acquiring a server certificate for TLS fails.
    /// </summary>
    public sealed class CertificateAcquireFailedException : SilicaException
    {
        public string ProviderName { get; }

        public CertificateAcquireFailedException(string providerName, Exception? inner = null)
            : base(
                code: FrontendExceptions.CertificateAcquireFailed.Code,
                message: $"Failed to acquire server certificate (provider='{providerName ?? "<unknown>"}').",
                category: FrontendExceptions.CertificateAcquireFailed.Category,
                exceptionId: FrontendExceptions.Ids.CertificateAcquireFailed,
                innerException: inner)
        {
            FrontendExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ProviderName = providerName ?? string.Empty;
        }
    }
}
