using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Optimization;

/// <summary>
/// The default protection entries and how user entries replace them.
///
/// Defaults are not stored: they are made from the software catalog (read once, shipped with the build) and the
/// system default each time the protection owner loads. A user entry replaces the default of the same target (same
/// entry id; for a software target also any user entry of that software identity) and is what the list shows,
/// carrying the replaced default's level and source.
/// </summary>
internal static class OptimizationProtectionDefaults
{
    /// <summary>
    /// scheduling_protection_2026-10-29 §3.2: every Windows system, component and service process is not frozen.
    /// Kernel processes (no user-mode image) are not process-policy candidates at all; the capability classifier
    /// owns that exclusion.
    /// </summary>
    internal static readonly IReadOnlyList<string> SystemDefaultKinds =
    [
        SoftwareKinds.WindowsSystem,
        SoftwareKinds.WindowsComponent,
        SoftwareKinds.WindowsService
    ];

    internal const string SoftwareCatalogOrigin = "software-catalog";
    internal const string SystemDefaultOrigin = "system-default";
    internal const string ProtectionListOrigin = "protection-list";

    internal static ProtectedOptimizationTarget[] Create(SoftwarePolicyProfile profile, DateTimeOffset loadedAt)
    {
        var entries = new List<ProtectedOptimizationTarget>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in profile.Assignments.Values.OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase))
        {
            if (profile.Groups.TryGetValue(assignment.AssignedGroupId, out var group))
            {
                AddSoftware(assignment.SoftwareId, assignment.DisplayName, null, group);
            }
        }

        foreach (var process in profile.ProcessAssignments)
        {
            if (process.AssignedGroupId is null || !profile.Groups.TryGetValue(process.AssignedGroupId, out var group))
            {
                continue;
            }

            if (process.ServiceSoftwareId is { } serviceSoftwareId)
            {
                AddSoftware(serviceSoftwareId, process.ServiceName, SoftwareKinds.WindowsService, group);
                continue;
            }

            if (group.NoFreeze)
            {
                Add(new ProtectedOptimizationTarget(
                    CreateId(OptimizationProtectionTargetTypes.Process, process.Key),
                    OptimizationProtectionTargetTypes.Process,
                    process.Key,
                    null,
                    null,
                    null,
                    loadedAt,
                    OptimizationProtectionSources.SoftwareCatalog,
                    SoftwareCatalogOrigin,
                    null,
                    OptimizationProtectionStates.Active,
                    AllowsPlacementAvoidance: true,
                    ProtectionLevel: OptimizationProtectionLevels.NoFreeze)
                {
                    PolicyGroupId = group.GroupId,
                    ProcessName = process.ProcessName,
                    ExecutablePath = process.ExecutablePath
                });
            }
        }

        foreach (var kind in SystemDefaultKinds)
        {
            var key = "software-kind:" + kind;
            Add(new ProtectedOptimizationTarget(
                CreateId(OptimizationProtectionTargetTypes.SoftwareKind, key),
                OptimizationProtectionTargetTypes.SoftwareKind,
                key,
                null,
                null,
                kind,
                loadedAt,
                OptimizationProtectionSources.SystemDefault,
                SystemDefaultOrigin,
                null,
                OptimizationProtectionStates.Active,
                AllowsPlacementAvoidance: true,
                ProtectionLevel: OptimizationProtectionLevels.NoFreeze));
        }

        return [.. entries];

        void AddSoftware(string softwareId, string? softwareName, string? softwareKind, SoftwarePolicyGroup group)
        {
            if (!group.NoFreeze)
            {
                return;
            }

            var key = SoftwareKey(softwareId);
            Add(new ProtectedOptimizationTarget(
                CreateId(OptimizationProtectionTargetTypes.Software, key),
                OptimizationProtectionTargetTypes.Software,
                key,
                softwareId,
                softwareName,
                softwareKind,
                loadedAt,
                OptimizationProtectionSources.SoftwareCatalog,
                SoftwareCatalogOrigin,
                null,
                OptimizationProtectionStates.Active,
                AllowsPlacementAvoidance: true,
                ProtectionLevel: OptimizationProtectionLevels.NoFreeze)
            {
                PolicyGroupId = group.GroupId
            });
        }

        void Add(ProtectedOptimizationTarget entry)
        {
            if (ids.Add(entry.Id))
            {
                entries.Add(entry);
            }
        }
    }

    /// <summary>The list: user entries (each carrying the default it replaces) and the defaults no user entry replaces.</summary>
    internal static ProtectedOptimizationTarget[] Merge(
        IReadOnlyList<ProtectedOptimizationTarget> defaults,
        IReadOnlyList<ProtectedOptimizationTarget> users)
    {
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProtectedOptimizationTarget>(defaults.Count + users.Count);
        foreach (var user in users)
        {
            var replacedDefault = FindReplacedDefault(defaults, user);
            if (replacedDefault is null)
            {
                result.Add(user);
                continue;
            }

            replaced.Add(replacedDefault.Id);
            result.Add(user with
            {
                DefaultProtectionLevel = replacedDefault.ProtectionLevel,
                DefaultProtectionSource = replacedDefault.ProtectionSource,
                PolicyGroupId = replacedDefault.PolicyGroupId ?? user.PolicyGroupId
            });
        }

        foreach (var entry in defaults)
        {
            if (!replaced.Contains(entry.Id))
            {
                result.Add(entry);
            }
        }

        return [.. result
            .OrderBy(static item => item.ProtectedAt)
            .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)];
    }

    internal static ProtectedOptimizationTarget? FindReplacedDefault(
        IReadOnlyList<ProtectedOptimizationTarget> defaults,
        ProtectedOptimizationTarget user)
    {
        foreach (var entry in defaults)
        {
            if (entry.Id.Equals(user.Id, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        if (user.TargetType != OptimizationProtectionTargetTypes.Software || string.IsNullOrWhiteSpace(user.SoftwareId))
        {
            return null;
        }

        foreach (var entry in defaults)
        {
            if (entry.TargetType == OptimizationProtectionTargetTypes.Software
                && string.Equals(entry.SoftwareId, user.SoftwareId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>A user entry for the target of <paramref name="replacedDefault"/>, at <paramref name="level"/>.</summary>
    internal static ProtectedOptimizationTarget CreateUserEntry(
        ProtectedOptimizationTarget replacedDefault,
        int level,
        string source,
        string origin,
        DateTimeOffset now)
        => new(
            replacedDefault.Id,
            replacedDefault.TargetType,
            replacedDefault.TargetKey,
            replacedDefault.SoftwareId,
            replacedDefault.SoftwareName,
            replacedDefault.SoftwareKind,
            now,
            source,
            origin,
            now,
            OptimizationProtectionStates.Active,
            AllowsPlacementAvoidance: true,
            ProtectionLevel: level)
        {
            PolicyGroupId = replacedDefault.PolicyGroupId,
            ProcessName = replacedDefault.ProcessName,
            ExecutablePath = replacedDefault.ExecutablePath
        };

    internal static string SoftwareKey(string softwareId) => "software:" + softwareId;

    internal static string CreateId(string targetType, string targetKey)
        => CreateStableId($"protect|{targetType}|{targetKey}");

    internal static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
