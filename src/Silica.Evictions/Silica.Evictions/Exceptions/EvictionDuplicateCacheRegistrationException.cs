using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionDuplicateCacheRegistrationException : SilicaException
    {
        public EvictionDuplicateCacheRegistrationException()
            : base(
                code: EvictionExceptions.DuplicateCacheRegistration.Code,
                message: "The cache is already registered with the eviction manager.",
                category: EvictionExceptions.DuplicateCacheRegistration.Category,
                exceptionId: EvictionExceptions.Ids.DuplicateCacheRegistration)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
