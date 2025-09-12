using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionNullKeyException : SilicaException
    {
        public EvictionNullKeyException()
            : base(
                code: EvictionExceptions.NullKey.Code,
                message: "The cache key cannot be null.",
                category: EvictionExceptions.NullKey.Category,
                exceptionId: EvictionExceptions.Ids.NullKey)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
