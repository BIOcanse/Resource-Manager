namespace ResourceManager.App.Domain.SystemHealth;

public static class SystemInterruptEventKinds
{
    public const string Isr = "ISR";
    public const string Dpc = "DPC";
    public const string ThreadedDpc = "ThreadedDPC";
    public const string TimerDpc = "TimerDPC";
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
