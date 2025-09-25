namespace Silica.Sessions.Contracts
{
    public enum SessionState
    {
        Created,
        Authenticated,
        Active,
        Idle,
        Closed,
        Expired
    }
}
