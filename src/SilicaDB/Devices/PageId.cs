namespace SilicaDB.Storage
{
    /// <summary>
    /// Strongly‐typed identifier for pages in a SilicaDB file.
    /// Wraps an underlying long to enforce domain invariants and improve readability.
    /// </summary>
    public readonly struct PageId : IEquatable<PageId>
    {
        public long Value { get; }

        public PageId(long value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "PageId must be nonnegative.");
            Value = value;
        }

        public override string ToString() => Value.ToString();

        public override bool Equals(object obj) =>
            obj is PageId other && Equals(other);

        public bool Equals(PageId other) =>
            Value == other.Value;

        public override int GetHashCode() =>
            Value.GetHashCode();

        public static bool operator ==(PageId a, PageId b) => a.Equals(b);
        public static bool operator !=(PageId a, PageId b) => !a.Equals(b);

        public static implicit operator long(PageId id) => id.Value;
        public static explicit operator PageId(long value) => new PageId(value);
    }
}
