namespace ResourceManager.App.Domain.Adaptation.Scheduling;

public sealed record ResourceManagerSelfSchedulingSnapshot(
    ResourceManagerSelfCpuGrade CpuGrade,
    ResourceManagerSelfGpuGrade GpuGrade,
    DateTimeOffset CpuUpdatedAt,
    DateTimeOffset GpuUpdatedAt,
    string CpuPolicyId,
    string GpuPolicyId,
    string CpuReason,
    string GpuReason,
    IReadOnlyList<ResourceManagerSelfSchedulingSource> Sources);

public sealed record ResourceManagerSelfSchedulingSource(
    string TargetId,
    string DisplayName,
    string PolicyId,
    ResourceManagerSelfCpuGrade CpuGrade,
    ResourceManagerSelfGpuGrade GpuGrade,
    DateTimeOffset UpdatedAt,
    string Reason);
