// File: Silica.Durability/WalRecordKind.cs
namespace Silica.Durability
{
    /// <summary>
    /// Optional, low-cardinality ARIES-aligned record kind codes.
    /// Durability does NOT enforce or parse these; Transactions owns the schema.
    /// </summary>
    public static class WalRecordKind
    {
        public const byte Begin = 0x10; // TX begin
        public const byte Commit = 0x11; // TX commit
        public const byte Abort = 0x12; // TX abort
        public const byte End = 0x13; // TX end (optional)
        public const byte Update = 0x20; // Data update (before/after or logical)
        public const byte CLR = 0x21; // Compensation log record
        public const byte BeginCkpt = 0x30; // Begin checkpoint
        public const byte EndCkpt = 0x31; // End checkpoint
        // Extend for system/internal as needed; keep values stable and low cardinality.
    }
}
