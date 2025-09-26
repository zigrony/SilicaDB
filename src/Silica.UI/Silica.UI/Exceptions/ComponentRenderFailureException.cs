using System;
using Silica.Exceptions;

namespace Silica.UI.Exceptions
{
    public sealed class ComponentRenderFailureException : SilicaException
    {
        public string ComponentId { get; }

        public ComponentRenderFailureException(string componentId, Exception? inner = null)
            : base(
                code: UIExceptions.ComponentRenderFailure.Code,
                message: $"Render failed for component '{componentId}'.",
                category: UIExceptions.ComponentRenderFailure.Category,
                exceptionId: UIExceptions.Ids.ComponentRenderFailure,
                innerException: inner)
        {
            UIExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ComponentId = componentId ?? string.Empty;
        }
    }
}
