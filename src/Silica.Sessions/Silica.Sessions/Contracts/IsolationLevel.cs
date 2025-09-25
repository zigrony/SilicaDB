namespace Silica.Sessions.Contracts
{
    public enum IsolationLevel
    {
        ReadUncommitted,
        ReadCommitted,
        RepeatableRead,
        Serializable,
        Snapshot
    }
}
