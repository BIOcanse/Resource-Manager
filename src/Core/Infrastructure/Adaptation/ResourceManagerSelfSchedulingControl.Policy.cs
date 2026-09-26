using ResourceManager.Adapter;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerSelfSchedulingControl
{
    public AdapterSoftwareSchedulingResult ApplyScheduling(AdapterSoftwareSchedulingEnvelope envelope)
    {
        appliedAt = DateTimeOffset.Now;
        if (!envelope.CpuGrade.HasValue && !envelope.GpuGrade.HasValue)
        {
            return new AdapterSoftwareSchedulingResult(
                envelope.PolicyId,
                false,
                null,
                null,
                false,
                false,
                "软件级调度信封必须至少包含一个 CPU 或 GPU 档位。",
                appliedAt.Value);
        }

        ResourceManagerSelfCpuGrade? cpuGrade = null;
        if (envelope.CpuGrade.HasValue)
        {
            if (!ResourceManagerSelfCpuGrades.TryFromAdapter(envelope.CpuGrade, out var resolvedCpuGrade))
            {
                return UnsupportedSchedulingResult(envelope);
            }

            cpuGrade = resolvedCpuGrade;
        }

        ResourceManagerSelfGpuGrade? gpuGrade = null;
        if (envelope.GpuGrade.HasValue)
        {
            if (!ResourceManagerSelfGpuGrades.TryFromAdapter(envelope.GpuGrade, out var resolvedGpuGrade))
            {
                return UnsupportedSchedulingResult(envelope);
            }

            gpuGrade = resolvedGpuGrade;
        }

        var outcome = ApplySoftwareSchedulingCore(envelope, cpuGrade, gpuGrade);
        return new AdapterSoftwareSchedulingResult(
            envelope.PolicyId,
            true,
            envelope.CpuGrade.HasValue
                ? ResourceManagerSelfCpuGrades.ToAdapter(outcome.Snapshot.CpuGrade)
                : null,
            envelope.GpuGrade.HasValue
                ? ResourceManagerSelfGpuGrades.ToAdapter(outcome.Snapshot.GpuGrade)
                : null,
            outcome.CpuChanged,
            outcome.GpuChanged,
            $"资源管理器自身已应用独立调度档位：CPU {ResourceManagerSelfCpuGrades.ToToken(outcome.Snapshot.CpuGrade)}，GPU {ResourceManagerSelfGpuGrades.ToToken(outcome.Snapshot.GpuGrade)}。",
            appliedAt.Value);
    }

    private AdapterSoftwareSchedulingResult UnsupportedSchedulingResult(
        AdapterSoftwareSchedulingEnvelope envelope)
    {
        return new AdapterSoftwareSchedulingResult(
            envelope.PolicyId,
            false,
            null,
            null,
            false,
            false,
            $"资源管理器自身只支持 CPU/GPU normal 和 optimize；拒绝 CPU {FormatRequestedCpuGrade(envelope.CpuGrade)}，GPU {FormatRequestedGpuGrade(envelope.GpuGrade)}。",
            appliedAt ?? DateTimeOffset.Now);
    }

    private static string FormatRequestedCpuGrade(AdapterCpuSchedulingGrade? grade)
    {
        return grade.HasValue ? AdapterCpuSchedulingGrades.ToToken(grade.Value) : "<none>";
    }

    private static string FormatRequestedGpuGrade(AdapterGpuSchedulingGrade? grade)
    {
        return grade.HasValue ? AdapterGpuSchedulingGrades.ToToken(grade.Value) : "<none>";
    }

}
