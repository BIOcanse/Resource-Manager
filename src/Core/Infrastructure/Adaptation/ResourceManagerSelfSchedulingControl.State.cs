using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerSelfSchedulingControl
{
    private readonly object schedulingGate = new();
    private readonly Dictionary<string, ResourceManagerSelfSchedulingSource> schedulingSources = new(StringComparer.OrdinalIgnoreCase);
    private ResourceManagerSelfCpuGrade currentCpuGrade = ResourceManagerSelfCpuGrade.Normal;
    private ResourceManagerSelfGpuGrade currentGpuGrade = ResourceManagerSelfGpuGrade.Normal;
    private DateTimeOffset cpuGradeUpdatedAt = DateTimeOffset.Now;
    private DateTimeOffset gpuGradeUpdatedAt = DateTimeOffset.Now;
    private string cpuGradePolicyId = "initial";
    private string gpuGradePolicyId = "initial";
    private string cpuGradeReason = "资源管理器自身 CPU 默认普通档。";
    private string gpuGradeReason = "资源管理器自身 GPU 默认普通档。";
    private DateTimeOffset? appliedAt;
}
