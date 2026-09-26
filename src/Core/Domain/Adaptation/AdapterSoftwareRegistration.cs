using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Domain.Adaptation;

public static class AdapterRegistrationSchemaVersions
{
    public const string Current = "2.0.0";
}

public static class AdapterResourceMarkerTransports
{
    public const string LoopbackHttp = "loopback-http";
}

public static class AdapterResourceMarkerStates
{
    public const string Online = "online";
    public const string Unreachable = "unreachable";
    public const string Unsupported = "unsupported";
}

public sealed record AdapterCpuSchedulingCapabilities(
    IReadOnlyList<AdapterCpuSchedulingGrade> SupportedGrades);

public sealed record AdapterGpuSchedulingCapabilities(
    IReadOnlyList<AdapterGpuSchedulingGrade> SupportedGrades);

public sealed record AdapterSoftwareSchedulingCapabilities(
    AdapterCpuSchedulingCapabilities? Cpu = null,
    AdapterGpuSchedulingCapabilities? Gpu = null)
{
    public bool HasAnyDimension => Cpu is not null || Gpu is not null;

    public AdapterSoftwareSchedulingCapabilities Normalize()
    {
        return this with
        {
            Cpu = Cpu is null
                ? null
                : new AdapterCpuSchedulingCapabilities(NormalizeGrades(Cpu.SupportedGrades)),
            Gpu = Gpu is null
                ? null
                : new AdapterGpuSchedulingCapabilities(NormalizeGrades(Gpu.SupportedGrades))
        };
    }

    private static IReadOnlyList<TGrade> NormalizeGrades<TGrade>(IEnumerable<TGrade>? grades)
        where TGrade : struct, Enum
    {
        return (grades ?? [])
            .Where(Enum.IsDefined)
            .Distinct()
            .Order()
            .ToArray();
    }
}

public sealed record AdapterSoftwareRegistrationRequest(
    string SchemaVersion,
    string AdapterId,
    string AppId,
    string DisplayName,
    string? Vendor,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<AdapterProcessDeclaration> Processes,
    IReadOnlyList<AdapterServiceDeclaration> Services,
    AdapterResourceMarkerEndpoint? ResourceMarkerEndpoint,
    AdapterSoftwareSchedulingCapabilities? SchedulingCapabilities = null);

public sealed record AdapterProcessDeclaration(
    string Name,
    int? ProcessId,
    string? ExecutablePath);

public sealed record AdapterServiceDeclaration(
    string Name,
    string? DisplayName);

public sealed record AdapterResourceMarkerEndpoint(
    string Transport,
    string Address);

public sealed record AdapterResourceMarkerProbeResult(
    string State,
    DateTimeOffset CheckedAt,
    int? StatusCode,
    string Message);

public sealed record AdapterSoftwareRegistration(
    string Id,
    string SchemaVersion,
    string AdapterId,
    string AppId,
    string DisplayName,
    string? Vendor,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<AdapterProcessDeclaration> Processes,
    IReadOnlyList<AdapterServiceDeclaration> Services,
    AdapterResourceMarkerEndpoint ResourceMarkerEndpoint,
    AdapterResourceMarkerProbeResult LastResourceMarkerProbe,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AdapterSoftwareSchedulingCapabilities? SchedulingCapabilities = null);
