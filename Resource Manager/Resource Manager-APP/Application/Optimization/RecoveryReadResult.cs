namespace ResourceManager.App.Application.Optimization;

public enum RecoveryReadStatus : byte
{
    Found = 1,
    NotFoundOrExited = 2,
    Unavailable = 3
}

public readonly record struct RecoveryReadResult<T>(
    RecoveryReadStatus Status,
    T? Value,
    int NativeErrorCode,
    string Message)
{
    public static RecoveryReadResult<T> Found(T value)
        => new(RecoveryReadStatus.Found, value, 0, string.Empty);

    public static RecoveryReadResult<T> NotFoundOrExited(int nativeErrorCode, string message)
        => new(RecoveryReadStatus.NotFoundOrExited, default, nativeErrorCode, message);

    public static RecoveryReadResult<T> Unavailable(int nativeErrorCode, string message)
        => new(RecoveryReadStatus.Unavailable, default, nativeErrorCode, message);
}

public sealed record ProcessInstanceRecoverySnapshot(
    int ProcessId,
    DateTimeOffset StartedAt);

[Flags]
public enum ProcessPlacementReadFields : byte
{
    None = 0,
    Affinity = 1 << 0,
    DefaultCpuSets = 1 << 1,
    PriorityClass = 1 << 2,
    MemoryPriority = 1 << 3
}

public sealed record ProcessPlacementRecoverySnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    string ProcessName,
    string? ExecutablePath,
    long? ProcessorAffinityMask,
    IReadOnlyList<uint>? DefaultCpuSetIds,
    string? PriorityClass,
    uint? MemoryPriority);
