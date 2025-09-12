using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionNullOnEvictedException : SilicaException
    {
        public EvictionNullOnEvictedException()
            : base(
                code: EvictionExceptions.NullOnEvicted.Code,
                message: "The eviction callback delegate cannot be null.",
                category: EvictionExceptions.NullOnEvicted.Category,
                exceptionId: EvictionExceptions.Ids.NullOnEvicted)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
