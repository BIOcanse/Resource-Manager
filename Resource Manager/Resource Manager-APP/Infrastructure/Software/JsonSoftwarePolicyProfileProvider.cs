using System.Text.Json;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Software;

public sealed class JsonSoftwarePolicyProfileProvider(IHostEnvironment environment) : ISoftwarePolicyProfileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string profilePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData",
        "SoftwareProfiles",
        "game-mode.local.json");
    private DateTime cachedLastWriteUtc = DateTime.MinValue;
    private SoftwarePolicyProfile cachedProfile = SoftwarePolicyProfile.Empty;

    public async Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(profilePath))
        {
            return SoftwarePolicyProfile.Empty;
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(profilePath);
        if (cachedLastWriteUtc == lastWriteUtc)
        {
            return cachedProfile;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(profilePath);
            if (cachedLastWriteUtc == lastWriteUtc)
            {
                return cachedProfile;
            }

            await using var stream = File.OpenRead(profilePath);
            var document = await JsonSerializer.DeserializeAsync<SoftwarePolicyProfileDocument>(stream, JsonOptions, cancellationToken);
            var profile = document is null ? SoftwarePolicyProfile.Empty : ToProfile(document);
            cachedProfile = profile;
            cachedLastWriteUtc = lastWriteUtc;
            return profile;
        }
        catch (JsonException)
        {
            cachedProfile = SoftwarePolicyProfile.Empty;
            cachedLastWriteUtc = lastWriteUtc;
            return cachedProfile;
        }
        finally
        {
            gate.Release();
        }
    }

    private static SoftwarePolicyProfile ToProfile(SoftwarePolicyProfileDocument document)
    {
        var groups = (document.SoftwareGroups ?? [])
            .Where(static group => !string.IsNullOrWhiteSpace(group.GroupId))
            .Select(static group => new SoftwarePolicyGroup(
                group.GroupId!.Trim(),
                string.IsNullOrWhiteSpace(group.DisplayName) ? group.GroupId!.Trim() : group.DisplayName!.Trim(),
                string.IsNullOrWhiteSpace(group.Role) ? null : group.Role!.Trim(),
                CleanStrings(group.Capabilities)))
            .GroupBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var assignments = (document.SoftwareAssignments ?? [])
            .Where(static assignment => !string.IsNullOrWhiteSpace(assignment.SoftwareId)
                && !string.IsNullOrWhiteSpace(assignment.AssignedGroupId))
            .Select(static assignment => new SoftwarePolicyAssignment(
                assignment.SoftwareId!.Trim(),
                string.IsNullOrWhiteSpace(assignment.DisplayName) ? assignment.SoftwareId!.Trim() : assignment.DisplayName!.Trim(),
                assignment.AssignedGroupId!.Trim(),
                CleanStrings(assignment.RootPaths)))
            .GroupBy(static assignment => assignment.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new SoftwarePolicyProfile(groups, assignments);
    }

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? values)
    {
        return (values ?? [])
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class SoftwarePolicyProfileDocument
    {
        public IReadOnlyList<SoftwarePolicyGroupDocument>? SoftwareGroups { get; set; }
        public IReadOnlyList<SoftwarePolicyAssignmentDocument>? SoftwareAssignments { get; set; }
    }

    private sealed class SoftwarePolicyGroupDocument
    {
        public string? GroupId { get; set; }
        public string? DisplayName { get; set; }
        public string? Role { get; set; }
        public IReadOnlyList<string>? Capabilities { get; set; }
    }

    private sealed class SoftwarePolicyAssignmentDocument
    {
        public string? SoftwareId { get; set; }
        public string? DisplayName { get; set; }
        public string? AssignedGroupId { get; set; }
        public IReadOnlyList<string>? RootPaths { get; set; }
    }
}
