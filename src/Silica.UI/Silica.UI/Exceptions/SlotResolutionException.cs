using System;
using Silica.Exceptions;

namespace Silica.UI.Exceptions
{
    public sealed class SlotResolutionException : SilicaException
    {
        public string SlotName { get; }
        public string ComponentId { get; }

        public SlotResolutionException(string slotName, string componentId)
            : base(
                code: UIExceptions.SlotResolutionError.Code,
                message: $"Failed to resolve slot '{slotName}' in component '{componentId}'.",
                category: UIExceptions.SlotResolutionError.Category,
                exceptionId: UIExceptions.Ids.SlotResolutionError)
        {
            UIExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SlotName = slotName ?? string.Empty;
            ComponentId = componentId ?? string.Empty;
        }
    }
}
