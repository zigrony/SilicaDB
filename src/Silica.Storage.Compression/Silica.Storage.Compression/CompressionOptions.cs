namespace Silica.Storage.Compression
{
    /// <summary>
    /// Options for configuring the compression mini-driver.
    /// </summary>
    public sealed class CompressionOptions
    {
        public string Algorithm { get; init; } = "deflate";
        public int Level { get; init; } = 1;
    }
}
