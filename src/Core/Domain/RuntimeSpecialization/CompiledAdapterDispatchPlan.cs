using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum CompiledAdapterDispatchRoute
{
    ConstraintActions = 0,
    SoftwareLevelScheduler = 1
}

public sealed record CompiledAdapterDispatchPlan(
    IReadOnlyDictionary<string, CompiledAdapterDispatchRoute> RoutesBySoftwareId,
    IReadOnlyDictionary<string, IReadOnlyList<AdapterCpuSchedulingGrade>> SupportedCpuGradesBySoftwareId,
    IReadOnlyDictionary<string, IReadOnlyList<AdapterGpuSchedulingGrade>> SupportedGpuGradesBySoftwareId)
{
    public static CompiledAdapterDispatchPlan Default { get; } = new(
        new Dictionary<string, CompiledAdapterDispatchRoute>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<AdapterCpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<AdapterGpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase));

    public CompiledAdapterDispatchRoute ResolveRoute(string? softwareId)
    {
        if (!string.IsNullOrWhiteSpace(softwareId)
            && RoutesBySoftwareId.TryGetValue(softwareId, out var route))
        {
            return route;
        }

        return CompiledAdapterDispatchRoute.ConstraintActions;
    }

    public IReadOnlyList<AdapterCpuSchedulingGrade> ResolveSupportedCpuGrades(string? softwareId)
    {
        return ResolveGrades(SupportedCpuGradesBySoftwareId, softwareId);
    }

    public IReadOnlyList<AdapterGpuSchedulingGrade> ResolveSupportedGpuGrades(string? softwareId)
    {
        return ResolveGrades(SupportedGpuGradesBySoftwareId, softwareId);
    }

    private static IReadOnlyList<TGrade> ResolveGrades<TGrade>(
        IReadOnlyDictionary<string, IReadOnlyList<TGrade>> gradesBySoftwareId,
        string? softwareId)
    {
        return !string.IsNullOrWhiteSpace(softwareId)
            && gradesBySoftwareId.TryGetValue(softwareId, out var grades)
            ? grades
            : [];
    }
}
