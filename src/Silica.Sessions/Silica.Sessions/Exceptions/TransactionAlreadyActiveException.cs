using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class TransactionAlreadyActiveException : SilicaException
    {
        public TransactionAlreadyActiveException()
            : base(
                code: SessionExceptions.TransactionAlreadyActive.Code,
                message: "A transaction is already active in this session.",
                category: SessionExceptions.TransactionAlreadyActive.Category,
                exceptionId: SessionExceptions.Ids.TransactionAlreadyActive)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
