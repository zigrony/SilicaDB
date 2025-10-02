public readonly struct PageVersion : IEquatable<PageVersion>
{
    public readonly ushort Major;
    public readonly ushort Minor;

    public PageVersion(ushort major, ushort minor)
    {
        Major = major;
        Minor = minor;
    }

    public bool Equals(PageVersion other) => Major == other.Major && Minor == other.Minor;
    public override bool Equals(object obj) => obj is PageVersion pv && Equals(pv);
    public override int GetHashCode() => (Major << 16) ^ Minor;
    public override string ToString() => Major + "." + Minor;
}
