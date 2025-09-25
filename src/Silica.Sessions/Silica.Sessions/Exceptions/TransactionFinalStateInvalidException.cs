using Silica.Exceptions;
using Silica.Sessions.Contracts;

namespace Silica.Sessions
{
    public sealed class TransactionFinalStateInvalidException : SilicaException
    {
        public TransactionState ProvidedState { get; }

        public TransactionFinalStateInvalidException(TransactionState state)
            : base(
                code: SessionExceptions.TransactionFinalStateInvalid.Code,
                message: "finalState must be Committed, RolledBack, or Aborted when ending a transaction.",
                category: SessionExceptions.TransactionFinalStateInvalid.Category,
                exceptionId: SessionExceptions.Ids.TransactionFinalStateInvalid)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ProvidedState = state;
        }
    }
}
