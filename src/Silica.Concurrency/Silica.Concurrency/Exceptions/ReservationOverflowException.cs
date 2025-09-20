using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class ReservationOverflowException : SilicaException
    {
        public string ResourceId { get; }

        public ReservationOverflowException(string resourceId, int currentReserved)
            : base(
                code: ConcurrencyExceptions.ReservationOverflow.Code,
                message: $"Reserved waiter count overflow for resource '{resourceId}'. CurrentReserved={currentReserved}.",
                category: ConcurrencyExceptions.ReservationOverflow.Category,
                exceptionId: ConcurrencyExceptions.Ids.ReservationOverflow)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ResourceId = resourceId ?? string.Empty;
        }
    }
}
