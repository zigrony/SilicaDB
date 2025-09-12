using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionNullValueFactoryException : SilicaException
    {
        public EvictionNullValueFactoryException()
            : base(
                code: EvictionExceptions.NullValueFactory.Code,
                message: "The value factory delegate cannot be null.",
                category: EvictionExceptions.NullValueFactory.Category,
                exceptionId: EvictionExceptions.Ids.NullValueFactory)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
