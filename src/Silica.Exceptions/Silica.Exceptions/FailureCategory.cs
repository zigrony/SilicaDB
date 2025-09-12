// Filename: Silica.Exceptions/Silica.Exceptions/FailureCategory.cs
namespace Silica.Exceptions
{
    /// <summary>
    /// Broad, low-cardinality categories for exception handling policy, routing, and analytics.
    /// </summary>
    public enum FailureCategory
    {
        Unknown = 0,
        Concurrency = 1,
        IO = 2,
        Validation = 3,
        Configuration = 4,
        Internal = 5,
        Timeout = 6,
        Cancelled = 7,
    }
}
