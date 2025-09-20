// Filename: Silica.Durability/Exceptions/PayloadTooLargeException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class PayloadTooLargeException : SilicaException
    {
        public int ProvidedLength { get; }
        public int MaxAllowed { get; }

        public PayloadTooLargeException(int providedLength, int maxAllowed)
            : base(
                code: DurabilityExceptions.PayloadTooLarge.Code,
                message: "Payload length exceeds maximum allowed size.",
                category: DurabilityExceptions.PayloadTooLarge.Category,
                exceptionId: DurabilityExceptions.Ids.PayloadTooLarge)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ProvidedLength = providedLength;
            MaxAllowed = maxAllowed;
        }
    }
}
