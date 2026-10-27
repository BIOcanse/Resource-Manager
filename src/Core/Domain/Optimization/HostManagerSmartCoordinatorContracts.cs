using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Domain.Optimization;

/// <summary>
/// 优化调度的当前状态，由协调器在读取时一次合成，消费端直接显示。
/// <para><see cref="SchedulerRunning"/>：调度循环存活且上一周期没有失败、当前模式启用调度、运行时没有故障关闭。</para>
/// <para><see cref="AppliedTargetCount"/>：产品当前持有改动的对象数（放置、进程策略、软件级档位、进程内存优先级），
/// 同一进程的多项改动只算一个对象。</para>
/// <para><see cref="FaultClosedReason"/>：null 表示运行时正常准入；非 null 表示运行时故障关闭、不会执行任何新调整，
/// 值是诊断原因码。</para>
/// </summary>
public sealed record HostManagerSmartCoordinatorStatus(
    string Mode,
    bool SchedulerRunning,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastRestoreAt,
    int PendingChangeCount,
    int AppliedTargetCount,
    string? FaultClosedReason,
    HostManagerCoordinatorFacts? Coordinator)
{
    /// <summary>The current settled segment's failure; null after success or before first settlement.</summary>
    public HostManagerCycleFailureDiagnostics? Failure { get; init; }
    public int FactExclusionCount { get; init; }
    public IReadOnlyList<HostManagerFactExclusionCount> FactExclusionCounts { get; init; } = [];
    public IReadOnlyList<HostManagerFactExclusionDiagnostics> FactExclusions { get; init; } = [];
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HostManagerCycleFailureKind>))]
public enum HostManagerCycleFailureKind
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("cycle-failed")] CycleFailed = 1,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("native-plan-failed")] NativePlanFailed = 2
}

// NativePlanStatus is the native plan's status code (for example "capacity-exceeded"), the same code
// the segment's native-plan exclusion carries as its reason; null unless Kind is NativePlanFailed.
public sealed record HostManagerCycleFailureDiagnostics(
    HostManagerCycleFailureKind Kind, string ExceptionType, SchedulingFactExclusionReason? NativePlanStatus);

public sealed record HostManagerFactExclusionCount(SchedulingFactCode Fact, SchedulingFactExclusionReason Reason, int Count);
public sealed record HostManagerFactExclusionDiagnostics(
    int? ProcessId, string? ProcessStartKey, string? TargetId, SchedulingFactCode Fact, SchedulingFactExclusionReason Reason, string? AdapterKey = null);

public sealed record HostManagerCoordinatorFacts(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Revision,
    uint PendingCount,
    uint InflightCount,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong ReasonMask);

public sealed record HostManagerSmartCoordinatorModeRequest(
    string Mode);
