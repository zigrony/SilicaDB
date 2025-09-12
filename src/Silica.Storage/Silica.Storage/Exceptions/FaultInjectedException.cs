using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class FaultInjectedException : SilicaException
    {
        public string FaultName { get; }

        public FaultInjectedException(string faultName)
            : base(
                code: StorageExceptions.FaultInjected.Code,
                message: $"Fault '{faultName}' injected for testing purposes.",
                category: StorageExceptions.FaultInjected.Category,
                exceptionId: StorageExceptions.Ids.FaultInjected)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FaultName = faultName;
        }
    }
}
