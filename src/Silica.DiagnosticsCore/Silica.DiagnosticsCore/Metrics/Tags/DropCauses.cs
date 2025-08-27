namespace Silica.DiagnosticsCore.Metrics
{
    public static class DropCauses
    {
        public const string OverflowDropNewest = "overflow_dropnewest";
        public const string OverflowDropOldest = "overflow_dropoldest";
        public const string CircuitOpen = "circuit_open";
        public const string WriteLatencyExceeded = "write_latency_exceeded";

        public const string UnknownMetricStrict = "unknown_metric_strict";
        public const string UnknownMetricNoAutoreg = "unknown_metric_no_autoreg";
        public const string TypeMismatch = "type_mismatch";
        public const string InnerError = "inner_error";
        public const string InnerException = "inner_exception";
        public const string InnerDisposed = "inner_disposed";
        public const string DispatcherDisposed = "dispatcher_disposed";
        public const string QueueFull = "queue_full";
        public const string UnregisteredSink = "unregistered_sink";
        public const string SinkError = "sink_error";
        public const string PumpFault = "pump_fault";
        public const string ShutdownBacklog = "dispatcher_shutdown_backlog";
        public const string SinkOverflow = "sink_overflow";
        public const string InvalidOperation = "invalid_operation";
        public const string InvalidStatus = "invalid_status";
        public const string NullMessage = "null_message";
        public const string InvalidLevel = "invalid_level";
        public const string Disabled = "disabled";
        public const string BelowMinimum = "below_minimum_level";
        public const string Sampled = "sampled";
        public const string Redacted = "redacted";
        public const string SinkInitFailed = "sink_init_failed";
        public const string Unredacted = "unredacted";
        public const string InvalidValue = "invalid_value";
    }
}
