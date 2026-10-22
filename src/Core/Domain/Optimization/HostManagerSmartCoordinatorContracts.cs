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
    string Message);

public sealed record HostManagerSmartCoordinatorModeRequest(
    string Mode);
