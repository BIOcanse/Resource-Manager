namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuExclusiveBindingSnapshot(
    DateTimeOffset CapturedAt,
    string CpuName,
    IReadOnlyList<CpuExclusiveBinding> Bindings);

public sealed record CpuExclusiveBinding(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<string> RequestedExclusivePositionIds,
    IReadOnlyList<string> ExpandedExclusivePhysicalCoreIds,
    IReadOnlyList<string> RequestedLockedPositionIds,
    IReadOnlyList<string> ExpandedLockedPhysicalCoreIds,
    bool LocksAffinity,
    bool AbsolutePerformanceModeEnabled,
    string MaximumOccupancyMode,
    DateTimeOffset UpdatedAt);
