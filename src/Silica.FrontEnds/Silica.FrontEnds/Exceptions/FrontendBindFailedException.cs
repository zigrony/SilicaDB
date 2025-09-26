// File: Silica.FrontEnds/Exceptions/FrontendBindFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.FrontEnds.Exceptions
{
    /// <summary>
    /// Thrown when a frontend fails to bind to its intended endpoints (HTTP/HTTPS).
    /// </summary>
    public sealed class FrontendBindFailedException : SilicaException
    {
        public string FrontendName { get; }
        public string Endpoint { get; }

        public FrontendBindFailedException(string frontendName, string endpoint, Exception? inner = null)
            : base(
                code: FrontendExceptions.FrontendBindFailed.Code,
                message: $"Frontend '{frontendName}' failed to bind endpoint '{endpoint}'.",
                category: FrontendExceptions.FrontendBindFailed.Category,
                exceptionId: FrontendExceptions.Ids.FrontendBindFailed,
                innerException: inner)
        {
            FrontendExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FrontendName = frontendName ?? string.Empty;
            Endpoint = endpoint ?? string.Empty;
        }
    }
}
