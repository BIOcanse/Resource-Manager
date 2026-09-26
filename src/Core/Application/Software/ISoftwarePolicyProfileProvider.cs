using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface ISoftwarePolicyProfileProvider
{
    Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken);
}

public sealed record SoftwarePolicyProfile(
    IReadOnlyDictionary<string, SoftwarePolicyGroup> Groups,
    IReadOnlyDictionary<string, SoftwarePolicyAssignment> Assignments)
{
    public static SoftwarePolicyProfile Empty { get; } = new(
        new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase));
}

public sealed record SoftwarePolicyGroup(
    string GroupId,
    string DisplayName,
    string? Role,
    IReadOnlyList<string> Capabilities);

public sealed record SoftwarePolicyAssignment(
    string SoftwareId,
    string DisplayName,
    string AssignedGroupId,
    IReadOnlyList<string> RootPaths);
