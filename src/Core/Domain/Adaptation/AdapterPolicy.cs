using ResourceManager.Adapter;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Domain.Adaptation;

public sealed record AdapterSoftwareSchedulingEnvelope(
    string PolicyId,
    DateTimeOffset GeneratedAt,
    string TargetId,
    string DisplayName,
    string? SoftwareId,
    AdapterCpuSchedulingGrade? CpuGrade,
    AdapterGpuSchedulingGrade? GpuGrade,
    double CpuScore,
    double GpuScore,
    string Reason);

public sealed record AdapterSoftwareSchedulingResult(
    string PolicyId,
    bool Accepted,
    AdapterCpuSchedulingGrade? AppliedCpuGrade,
    AdapterGpuSchedulingGrade? AppliedGpuGrade,
    bool CpuChanged,
    bool GpuChanged,
    string Message,
    DateTimeOffset AppliedAt);
