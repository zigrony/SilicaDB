namespace Silica.Common.Primitives
{
    /// <summary>
    /// Stable page type identifiers. Low-cardinality, engine-wide constants.
    /// </summary>
    public enum PageType : byte
    {
        Unknown = 0,
        Header = 1,
        Catalog = 2,
        PFS = 3,
        GAM = 4,
        SGAM = 5,
        DCM = 6,
        BCM = 7,
        IAM = 8,
        Data = 10,
        Index = 11,
        Lob = 12,
        Free = 254
    }

}
