namespace Silica.StorageAllocation.Sql
{
    /// <summary>
    /// Declarative geometry/configuration for SQL allocation maps.
    /// Later, this will define extent size, map page intervals, etc.
    /// </summary>
    public sealed class SqlAllocationManifest
    {
        public int ExtentSizePages { get; }
        public int GamIntervalPages { get; }
        public int PfsIntervalPages { get; }

        public SqlAllocationManifest(
            int extentSizePages = 8,
            int gamIntervalPages = 511232, // placeholder
            int pfsIntervalPages = 8088)   // placeholder
        {
            ExtentSizePages = extentSizePages;
            GamIntervalPages = gamIntervalPages;
            PfsIntervalPages = pfsIntervalPages;
        }
    }
}
