using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionManagerDisposedException : SilicaException
    {
        public EvictionManagerDisposedException()
            : base(
                code: EvictionExceptions.ManagerDisposed.Code,
                message: "The eviction manager has been disposed and can no longer be used.",
                category: EvictionExceptions.ManagerDisposed.Category,
                exceptionId: EvictionExceptions.Ids.ManagerDisposed)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
