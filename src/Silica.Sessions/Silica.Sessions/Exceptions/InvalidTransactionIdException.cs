using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class InvalidTransactionIdException : SilicaException
    {
        public long TransactionId { get; }

        public InvalidTransactionIdException(long transactionId)
            : base(
                code: SessionExceptions.InvalidTransactionId.Code,
                message: $"TransactionId must be > 0. Provided: {transactionId}.",
                category: SessionExceptions.InvalidTransactionId.Category,
                exceptionId: SessionExceptions.Ids.InvalidTransactionId)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TransactionId = transactionId;
        }
    }
}
