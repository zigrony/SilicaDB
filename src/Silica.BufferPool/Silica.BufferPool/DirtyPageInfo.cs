using Silica.Common.Primitives;
// Filename: Silica.BufferPool/DirtyPageInfo.cs
namespace Silica.BufferPool
{
    /// <summary>
    /// Snapshot record for a dirty page: page identity and pageLSN.
    /// </summary>
    public readonly record struct DirtyPageInfo(PageId PageId, long PageLsn);
}
