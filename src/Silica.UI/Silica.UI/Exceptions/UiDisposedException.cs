using System;
using Silica.Exceptions;

namespace Silica.UI.Exceptions
{
    public sealed class UiDisposedException : SilicaException
    {
        public UiDisposedException()
            : base(
                code: UIExceptions.UiDisposed.Code,
                message: "Operation attempted on a disposed UI subsystem.",
                category: UIExceptions.UiDisposed.Category,
                exceptionId: UIExceptions.Ids.UiDisposed)
        {
            UIExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
