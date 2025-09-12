using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionNullCacheRegistrationException : SilicaException
    {
        public EvictionNullCacheRegistrationException()
            : base(
                code: EvictionExceptions.NullCacheRegistration.Code,
                message: "The cache instance to register cannot be null.",
                category: EvictionExceptions.NullCacheRegistration.Category,
                exceptionId: EvictionExceptions.Ids.NullCacheRegistration)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
