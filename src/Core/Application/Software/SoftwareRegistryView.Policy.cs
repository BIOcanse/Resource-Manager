using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static void ApplyPolicyProfile(List<SoftwareRecord> records, SoftwarePolicyProfile profile)
    {
        if (profile.Assignments.Count == 0)
        {
            return;
        }

        for (var index = 0; index < records.Count; index++)
        {
            records[index] = ApplyPolicyProfile(records[index], profile);
        }
    }

    private static SoftwareRecord ApplyPolicyProfile(SoftwareRecord record, SoftwarePolicyProfile profile)
    {
        if (!record.IdentityConfirmed
            || !profile.Assignments.TryGetValue(record.Id, out var assignment)
            || !profile.Groups.TryGetValue(assignment.AssignedGroupId, out var group))
        {
            return record;
        }

        var policyKind = ResolvePolicyProjectedKind(group);
        var projectedKind = policyKind ?? record.Kind;
        var roots = ResolvePolicyRootPaths(record.RootPaths, assignment.RootPaths, projectedKind);
        var sources = record.SourceFacts
            .Append(new SoftwareSourceFact(SoftwareSourceKinds.Policy, PolicyGroupId: group.GroupId, PolicyGroupName: group.DisplayName))
            .Distinct()
            .ToArray();

        return record with
        {
            Kind = projectedKind,
            HasExplicitKind = record.HasExplicitKind || policyKind is not null,
            SourceFacts = sources,
            RootPaths = roots
        };
    }

    private static string? ResolvePolicyProjectedKind(SoftwarePolicyGroup group)
    {
        if (group.GroupId.Equals("targets.games", StringComparison.OrdinalIgnoreCase)
            || string.Equals(group.Role, "TargetCandidate", StringComparison.OrdinalIgnoreCase))
        {
            return SoftwareKinds.Game;
        }

        if (group.Capabilities.Contains("HighPerformanceCandidate", StringComparer.OrdinalIgnoreCase))
        {
            return SoftwareKinds.HighPerformance;
        }

        return null;
    }

    private static IReadOnlyList<string> ResolvePolicyRootPaths(
        IReadOnlyList<string> recordRoots,
        IReadOnlyList<string> assignmentRoots,
        string projectedKind)
    {
        var roots = (assignmentRoots.Count > 0 ? assignmentRoots : recordRoots)
            .Select(NormalizePathForCompare)
            .Where(static root => root is not null)
            .Select(static root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!projectedKind.Equals(SoftwareKinds.Game, StringComparison.OrdinalIgnoreCase))
        {
            return roots;
        }

        return roots
            .Where(root => !IsBroadLauncherOrSteamRoot(root))
            .Where(root => !roots.Any(other => !other.Equals(root, StringComparison.OrdinalIgnoreCase)
                && IsSameOrUnder(other, root)))
            .ToArray();
    }

    private static bool IsBroadLauncherOrSteamRoot(string root)
    {
        var normalized = root.Replace('/', '\\');
        if (normalized.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leaf = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return normalized.EndsWith(@"\steam", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\steamapps", StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("启动器", StringComparison.OrdinalIgnoreCase);
    }

}
