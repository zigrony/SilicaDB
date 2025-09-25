namespace Silica.Sessions.Contracts
{
    public enum TransactionState
    {
        None,
        Active,
        Committed,
        RolledBack,
        Aborted
    }
}
