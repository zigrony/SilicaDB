using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class LockManagerDisposedException : SilicaException
    {
        public LockManagerDisposedException()
            : base(
                code: ConcurrencyExceptions.LockManagerDisposed.Code,
                message: "Operation attempted on a disposed LockManager.",
                category: ConcurrencyExceptions.LockManagerDisposed.Category,
                exceptionId: ConcurrencyExceptions.Ids.LockManagerDisposed)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
