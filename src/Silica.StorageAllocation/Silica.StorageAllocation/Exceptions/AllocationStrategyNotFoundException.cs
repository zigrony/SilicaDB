using Silica.Exceptions;

namespace Silica.Storage.Allocation.Exceptions
{
    public sealed class AllocationStrategyNotFoundException : SilicaException
    {
        public AllocationStrategyNotFoundException(string strategyName)
            : base(
                code: StorageAllocationExceptions.StrategyNotFound.Code,
                message: $"No allocation strategy registered with name '{strategyName}'.",
                category: StorageAllocationExceptions.StrategyNotFound.Category,
                exceptionId: StorageAllocationExceptions.Ids.StrategyNotFound)
        {
            StorageAllocationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            StrategyName = strategyName;
        }

        public string StrategyName { get; }
    }
}
