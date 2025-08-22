using System;

namespace Silica.DiagnosticsCore.Metrics
{
    /// <summary>
    /// Well‑known tag key names used across Silica metrics.
    /// Keep values short, lowercase, and consistent to maximize exporter efficiency.
    /// </summary>
    public static class TagKeys
    {
        public const string Pool = "pool";         // Buffer pool name
        public const string Device = "device";       // Physical or logical device ID
        public const string Operation = "op";           // Operation type (read, write, evict, etc.)
        public const string Component = "component";    // Subsystem or module name
        public const string Tenant = "tenant";       // Multi‑tenant identifier
        public const string Status = "status";       // Success, failure, retry, etc.
        public const string Exception = "exception";    // Exception type name
        public const string Shard = "shard";        // Shard or partition identifier
        public const string Thread = "thread";       // Thread or worker identifier
        public const string Concurrency = "concurrency";  // Degree of concurrency at measurement time
        public const string DropCause = "drop_cause";    // Reason for trace drop
        public const string Region = "region";
        public const string WidgetId = "widget_id";
        public const string Policy = "policy";
        public const string Sink = "sink";
        public const string Field = "field";
    }
}
