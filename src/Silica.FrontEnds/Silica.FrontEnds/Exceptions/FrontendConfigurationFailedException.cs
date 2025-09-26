// File: Silica.FrontEnds/Exceptions/FrontendConfigurationFailedException.cs
using System;
using Silica.Exceptions;

namespace Silica.FrontEnds.Exceptions
{
    /// <summary>
    /// Thrown when a frontend fails during Configure in the registry orchestration.
    /// </summary>
    public sealed class FrontendConfigurationFailedException : SilicaException
    {
        public string FrontendName { get; }

        public FrontendConfigurationFailedException(string frontendName, Exception? inner = null)
            : base(
                code: FrontendExceptions.FrontendConfigurationFailed.Code,
                message: $"Frontend '{frontendName}' configuration failed.",
                category: FrontendExceptions.FrontendConfigurationFailed.Category,
                exceptionId: FrontendExceptions.Ids.FrontendConfigurationFailed,
                innerException: inner)
        {
            FrontendExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FrontendName = frontendName ?? string.Empty;
        }
    }
}
