namespace Silica.Storage.SqlMinidriver
{
    /// <summary>
    /// Options for configuring the Sql mini-driver.
    /// </summary>
    public sealed class SqlOptions
    {
        public int PageSize { get; init; }
        public int ReservedSystemPages { get; init; }
        public int AllocationMapInterval { get; init; }
        public int InitialAllocationMaps { get; init; }
        public int DatabaseHeaderPage { get; init; }
        public int Version { get; init; }
        public uint Signature { get; init; }

    }

}
