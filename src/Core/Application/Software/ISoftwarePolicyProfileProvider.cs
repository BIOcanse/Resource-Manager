using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface ISoftwarePolicyProfileProvider
{
    Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The software catalog shipped with the build (<c>Configuration/SoftwareProfiles/official.json</c>): groups,
/// the software identities assigned to them, and per-process assignments matched by process name (and path)
/// or by Windows service name.
/// </summary>
public sealed record SoftwarePolicyProfile(
    IReadOnlyDictionary<string, SoftwarePolicyGroup> Groups,
    IReadOnlyDictionary<string, SoftwarePolicyAssignment> Assignments)
{
    public static SoftwarePolicyProfile Empty { get; } = new(
        new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase));

    /// <summary>In catalog order; every entry names a process or a service and assigns a group and/or a base score.</summary>
    public IReadOnlyList<SoftwarePolicyProcessAssignment> ProcessAssignments { get; init; } = [];

    /// <summary>
    /// Catalog layer "目录组" of the base-score chain for one software identity: the base score of the group the
    /// identity is assigned to, or, for the identity of a process hosting a single Windows service
    /// (<c>windows-service:&lt;name&gt;</c>), the score of that service's catalog entry. Null when the catalog
    /// gives this identity no base score.
    /// </summary>
    public SoftwarePolicyCatalogBaseScore? ResolveSoftwareBaseScore(string? softwareId)
    {
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            return null;
        }

        if (Assignments.TryGetValue(softwareId, out var assignment)
            && Groups.TryGetValue(assignment.AssignedGroupId, out var group)
            && group.BaseScore is { } groupScore)
        {
            return new(groupScore, group.GroupId, null);
        }

        foreach (var entry in ProcessAssignments)
        {
            if (entry.ServiceSoftwareId is { } serviceSoftwareId
                && serviceSoftwareId.Equals(softwareId, StringComparison.OrdinalIgnoreCase)
                && ResolveEntryScore(entry) is { } score)
            {
                return score;
            }
        }

        return null;
    }

    /// <summary>
    /// Catalog layer "目录按进程" of the base-score chain for one process: the first process-name entry that
    /// matches (an entry limited to an executable path before a name-only entry), else the highest score among
    /// the service entries of <paramref name="serviceNames"/> (the services the process hosts; pass them for a
    /// shared service host). An entry's score is its own <c>baseScore</c>, else its group's. Null when no matching
    /// entry gives a score.
    /// </summary>
    public SoftwarePolicyCatalogBaseScore? ResolveProcessBaseScore(
        string? processName,
        string? executablePath,
        IReadOnlyList<string>? serviceNames = null)
    {
        IReadOnlyList<string> names = processName is null ? [] : [processName];
        IReadOnlyList<string> paths = executablePath is null ? [] : [executablePath];
        SoftwarePolicyCatalogBaseScore? nameOnly = null;
        foreach (var entry in ProcessAssignments)
        {
            if (entry.ProcessName is null
                || !SoftwareProcessMatch.Matches(entry.ProcessName, entry.MatchExecutablePath, names, paths)
                || ResolveEntryScore(entry) is not { } score)
            {
                continue;
            }

            if (entry.MatchExecutablePath is not null)
            {
                return score;
            }

            nameOnly ??= score;
        }

        if (nameOnly is not null || serviceNames is not { Count: > 0 })
        {
            return nameOnly;
        }

        SoftwarePolicyCatalogBaseScore? service = null;
        foreach (var entry in ProcessAssignments)
        {
            if (entry.ServiceName is null
                || !serviceNames.Contains(entry.ServiceName, StringComparer.OrdinalIgnoreCase)
                || ResolveEntryScore(entry) is not { } score)
            {
                continue;
            }

            if (service is null || score.BaseScore > service.BaseScore)
            {
                service = score;
            }
        }

        return service;
    }

    private SoftwarePolicyCatalogBaseScore? ResolveEntryScore(SoftwarePolicyProcessAssignment entry)
    {
        if (entry.BaseScore is { } own)
        {
            return new(own, entry.AssignedGroupId, entry.Key);
        }

        return entry.AssignedGroupId is { } groupId
            && Groups.TryGetValue(groupId, out var group)
            && group.BaseScore is { } groupScore
                ? new(groupScore, group.GroupId, entry.Key)
                : null;
    }
}

/// <summary>A catalog base score and where it comes from: the group, and the per-process entry when one matched.</summary>
public sealed record SoftwarePolicyCatalogBaseScore(
    double BaseScore,
    string? GroupId,
    string? ProcessEntryKey);

public sealed record SoftwarePolicyGroup(
    string GroupId,
    string DisplayName,
    string? Role,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>
    /// Base score (0..100) of members of this group; null keeps the layer below. "Not suppressed" is a score in
    /// the high tier (&gt;= 81), not a protection.
    /// </summary>
    public double? BaseScore { get; init; }

    /// <summary>Members are not frozen: the group gives them a default protection entry (source <c>software_catalog</c>).</summary>
    public bool NoFreeze { get; init; }
}

public sealed record SoftwarePolicyAssignment(
    string SoftwareId,
    string DisplayName,
    string AssignedGroupId,
    IReadOnlyList<string> RootPaths);

/// <summary>
/// A per-process catalog entry. Exactly one of <see cref="ProcessName"/> (normalized, see
/// <see cref="SoftwareProcessMatch"/>; optionally limited to <see cref="ExecutablePath"/>) and
/// <see cref="ServiceName"/> is set; at least one of <see cref="AssignedGroupId"/> and <see cref="BaseScore"/>.
/// A match takes precedence over the group of the software the process is attributed to.
/// </summary>
public sealed record SoftwarePolicyProcessAssignment(
    string Key,
    string? ProcessName,
    string? ExecutablePath,
    string? ServiceName,
    string? AssignedGroupId,
    double? BaseScore)
{
    /// <summary><see cref="ExecutablePath"/> with environment variables expanded, for matching.</summary>
    public string? MatchExecutablePath { get; } = SoftwareProcessMatch.NormalizeExecutablePath(ExecutablePath);

    /// <summary>Service entries: the software identity of a process that hosts only this service.</summary>
    public string? ServiceSoftwareId { get; } = ServiceName is null ? null : WindowsServiceSoftwareIds.ForService(ServiceName);

    public static string CreateKey(string? processName, string? executablePath, string? serviceName)
    {
        if (serviceName is not null)
        {
            return "service:" + WindowsServiceSoftwareIds.NormalizePart(serviceName);
        }

        return executablePath is not null
            ? "process-path:" + executablePath.Trim().ToLowerInvariant()
            : "process-name:" + processName!.ToLowerInvariant();
    }
}
