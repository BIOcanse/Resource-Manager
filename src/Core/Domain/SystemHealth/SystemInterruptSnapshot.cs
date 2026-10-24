namespace ResourceManager.App.Domain.SystemHealth;

public static class SystemInterruptProviderStates
{
    public const string Running = "running";
    public const string Warming = "warming";
    public const string Frozen = "frozen";
    public const string Unavailable = "unavailable";
    public const string Idle = "idle";
    public const string Starting = "starting";
    public const string Stopped = "stopped";
    public const string StopFailed = "stop_failed";
    public const string Available = "available";
    public const string NotStarted = "not_started";
}

public static class SystemInterruptEventKinds
{
    public const string Isr = "isr";
    public const string Dpc = "dpc";
    public const string ThreadedDpc = "threaded_dpc";
    public const string TimerDpc = "timer_dpc";
}

public static class SystemInterruptMeasurementSemantics
{
    public const double LongEventMilliseconds = 1;
}

public sealed record SystemInterruptSnapshot(
    DateTimeOffset CapturedAt,
    bool Available,
    long SourceGeneration,
    TimeSpan Window,
    int LogicalProcessorCount,
    double TotalDurationMilliseconds,
    double MaximumSingleDurationMilliseconds,
    string? MaximumEventKind,
    string? MaximumDriverName,
    int EventCount,
    int EventsAtOrAboveOneMillisecond,
    double CpuCapacityPercent,
    IReadOnlyList<SystemInterruptBucketSnapshot> Buckets,
    IReadOnlyList<SystemInterruptDriverSnapshot> Drivers,
    SystemInterruptProviderState ProviderState)
{
    public static SystemInterruptSnapshot Unavailable(string state, string message)
    {
        return new SystemInterruptSnapshot(
            DateTimeOffset.UtcNow,
            false,
            0,
            TimeSpan.Zero,
            Math.Max(1, Environment.ProcessorCount),
            0,
            0,
            null,
            null,
            0,
            0,
            0,
            [],
            [],
            new SystemInterruptProviderState("etw-system-interrupts", state, message));
    }
}

public sealed record SystemInterruptBucketSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double TotalDurationMilliseconds,
    double CpuCapacityPercent,
    int EventCount);

public sealed record SystemInterruptDriverSnapshot(
    string ModuleName,
    string? ModulePath,
    double TotalDurationMilliseconds,
    double MaximumSingleDurationMilliseconds,
    int EventCount,
    int EventsAtOrAboveOneMillisecond,
    double CpuCapacityPercent);

public sealed record SystemInterruptProviderState(
    string ProviderId,
    string State,
    string Message,
    int EventsLost = 0);
