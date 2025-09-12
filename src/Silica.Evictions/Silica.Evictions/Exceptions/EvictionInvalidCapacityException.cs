using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionInvalidCapacityException : SilicaException
    {
        public int Capacity { get; }

        public EvictionInvalidCapacityException(int capacity)
            : base(
                code: EvictionExceptions.InvalidCapacity.Code,
                message: $"Invalid capacity: {capacity}",
                category: EvictionExceptions.InvalidCapacity.Category,
                exceptionId: EvictionExceptions.Ids.InvalidCapacity)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Capacity = capacity;
        }
    }
}
