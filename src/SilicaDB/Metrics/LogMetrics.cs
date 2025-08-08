using SilicaDB.Metrics;

public static class LogMetrics
{
    public static readonly MetricDefinition EmittedInfo = new(
        name: "logs.emitted.INFO",
        type: MetricType.Counter,
        description: "Count of INFO-level logs emitted",
        unit: "entries",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger"),
            ("severity", "INFO")
        }
    );

    public static readonly MetricDefinition EmittedWarning = new(
        name: "logs.emitted.WARNING",
        type: MetricType.Counter,
        description: "Count of WARNING-level logs emitted",
        unit: "entries",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger"),
            ("severity", "WARNING")
        }
    );

    public static readonly MetricDefinition EmittedError = new(
        name: "logs.emitted.ERROR",
        type: MetricType.Counter,
        description: "Count of ERROR-level logs emitted",
        unit: "entries",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger"),
            ("severity", "ERROR")
        }
    );

    public static readonly MetricDefinition Dropped = new(
        name: "logs.dropped",
        type: MetricType.Counter,
        description: "Count of logs dropped due to filtering or overflow",
        unit: "entries",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger")
        }
    );

    public static readonly MetricDefinition FlushLatencyMicros = new(
        name: "logs.flush.latency.micros",
        type: MetricType.Histogram,
        description: "Latency of flushing log batches in microseconds",
        unit: "μs",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger")
        }
    );

    public static readonly MetricDefinition FormatLatencyMicros = new(
        name: "logs.format.latency.micros",
        type: MetricType.Histogram,
        description: "Time spent formatting log messages in microseconds",
        unit: "μs",
        defaultTags: new (string, object)[]
        {
            ("component", "SilicaLogger")
        }
    );
}
