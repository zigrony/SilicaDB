namespace Silica.Sessions.Contracts
{
    /// <summary>
    /// Strongly typed session identifier.
    /// </summary>
    public readonly struct SessionId
    {
        public Guid Value { get; }

        public SessionId(Guid value)
        {
            Value = value;
        }

        public static SessionId NewSession() => new SessionId(Guid.NewGuid());

        public override string ToString() => Value.ToString();
    }
}
