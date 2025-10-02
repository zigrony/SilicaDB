namespace Silica.Common.Primitives
{
    /// <summary>
    /// Logical identity of a page: (FileId, PageIndex).
    /// Allocated only via StorageAllocation, consumed everywhere.
    /// </summary>
    public readonly record struct PageId(long FileId, long PageIndex);
}
