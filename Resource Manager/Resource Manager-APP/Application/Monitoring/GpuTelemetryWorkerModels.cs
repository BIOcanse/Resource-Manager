namespace ResourceManager.App.Application.Monitoring;

public enum GpuTelemetryWorkerStatus : byte
{
    Disabled = 0,
    NotConfigured = 1,
    Starting = 2,
    Online = 3,
    Stale = 4,
    TimedOut = 5,
    Failed = 6,
    Unavailable = 7
}

public enum GpuTelemetryWorkerDetailLevel : byte
{
    LowIntrusion = 0,
    VendorSensors = 1,
    AdvancedCounters = 2,
    Profiling = 3
}

public enum GpuTelemetryCounterClass : byte
{
    Engine = 0,
    Sensor = 1,
    VendorUtilization = 2,
    HardwareCounter = 3,
    ProfilerMetric = 4
}

public sealed record GpuTelemetryWorkerRequest(
    byte ProtocolVersion,
    string[] CounterIds,
    GpuTelemetryWorkerDetailLevel DetailLevel,
    int TimeoutMilliseconds)
{
    public const byte CurrentProtocolVersion = 1;

    public static GpuTelemetryWorkerRequest Create(
        IEnumerable<string>? counterIds = null,
        GpuTelemetryWorkerDetailLevel detailLevel = GpuTelemetryWorkerDetailLevel.LowIntrusion,
        int timeoutMilliseconds = 250)
    {
        return new GpuTelemetryWorkerRequest(
            CurrentProtocolVersion,
            NormalizeCounterIds(counterIds),
            detailLevel,
            Math.Max(1, timeoutMilliseconds));
    }

    private static string[] NormalizeCounterIds(IEnumerable<string>? counterIds)
    {
        if (counterIds is null)
        {
            return [];
        }

        return counterIds
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record GpuTelemetryWorkerSnapshot(
    byte ProtocolVersion,
    long Sequence,
    DateTimeOffset CapturedAt,
    long StaleMilliseconds,
    GpuTelemetryWorkerStatus Status,
    GpuTelemetryAdapterSnapshot[] Adapters,
    GpuTelemetryProviderState[] Providers,
    string? Message)
{
    public const byte CurrentProtocolVersion = GpuTelemetryWorkerRequest.CurrentProtocolVersion;

    public static GpuTelemetryWorkerSnapshot Unavailable(string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new GpuTelemetryWorkerSnapshot(
            CurrentProtocolVersion,
            0,
            now,
            0,
            GpuTelemetryWorkerStatus.Unavailable,
            [],
            [],
            message);
    }

    public GpuTelemetryWorkerSnapshot WithStaleAge(DateTimeOffset now)
    {
        return this with
        {
            StaleMilliseconds = Math.Max(0, (long)(now - CapturedAt).TotalMilliseconds)
        };
    }
}

public sealed record GpuTelemetryAdapterSnapshot(
    int AdapterIndex,
    ulong AdapterIdentity,
    string? AdapterName,
    string? VendorId,
    string? Architecture,
    GpuTelemetryCounterSample[] Counters);

public sealed record GpuTelemetryCounterSample(
    string CounterId,
    string DisplayName,
    GpuTelemetryCounterClass CounterClass,
    double? Value,
    string? Unit,
    string ProviderId,
    bool IsIntrusive,
    string? EngineName);

public sealed record GpuTelemetryProviderState(
    string ProviderId,
    GpuTelemetryWorkerStatus Status,
    string? Message,
    bool IsOutOfProcess,
    bool IsProfilingProvider,
    int SampleDurationMilliseconds);
