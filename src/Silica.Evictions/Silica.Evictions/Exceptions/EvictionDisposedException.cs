using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionDisposedException : SilicaException
    {
        public EvictionDisposedException()
            : base(
                code: EvictionExceptions.CacheDisposed.Code,
                message: "The cache has been disposed and can no longer be used.",
                category: EvictionExceptions.CacheDisposed.Category,
                exceptionId: EvictionExceptions.Ids.CacheDisposed)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
