using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.Optimization;

/// <summary>
/// Resolves the protection of one process from the protection list (<c>IOptimizationProtectionService
/// .GetProtectedTargetsAsync</c>, which already holds one effective entry per target: the user's when the user
/// changed it, else the default). Only active entries take part.
///
/// The most specific target that has an entry decides, even at level 0 (a user who switched a target's default
/// off keeps it off). The result is 0 (none) or 1 (no freeze):
/// <list type="number">
/// <item>process: a process entry matching the image name and path, else one matching the name only;</item>
/// <item>software: the entry of the software identity the process is attributed to;</item>
/// <item>hosted services: for a shared service host, the highest entry among the services it hosts
/// (<c>windows-service:&lt;name&gt;</c> entries);</item>
/// <item>kind: the entry of the software kind (the Windows system, component and service defaults).</item>
/// </list>
/// Several entries matching at one step resolve to the highest level.
/// Kernel processes (no user-mode image) need no rule here: they never become process-policy candidates.
/// </summary>
public sealed class OptimizationProtectionTable
{
    private readonly ProcessEntry[] processEntries;
    private readonly Dictionary<string, ProtectedOptimizationTarget> softwareEntries;
    private readonly Dictionary<string, ProtectedOptimizationTarget> kindEntries;

    private OptimizationProtectionTable(
        ProcessEntry[] processEntries,
        Dictionary<string, ProtectedOptimizationTarget> softwareEntries,
        Dictionary<string, ProtectedOptimizationTarget> kindEntries)
    {
        this.processEntries = processEntries;
        this.softwareEntries = softwareEntries;
        this.kindEntries = kindEntries;
    }

    public static OptimizationProtectionTable Create(IReadOnlyList<ProtectedOptimizationTarget> entries)
    {
        var processes = new List<ProcessEntry>();
        var software = new Dictionary<string, ProtectedOptimizationTarget>(StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, ProtectedOptimizationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.State != OptimizationProtectionStates.Active)
            {
                continue;
            }

            switch (entry.TargetType)
            {
                case OptimizationProtectionTargetTypes.Process:
                    if (SoftwareProcessMatch.NormalizeProcessName(entry.ProcessName) is { } name)
                    {
                        processes.Add(new(entry, name, SoftwareProcessMatch.NormalizeExecutablePath(entry.ExecutablePath)));
                    }
                    break;
                case OptimizationProtectionTargetTypes.SoftwareKind:
                    if (!string.IsNullOrWhiteSpace(entry.SoftwareKind))
                    {
                        KeepHigher(kinds, entry.SoftwareKind, entry);
                    }
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(entry.SoftwareId))
                    {
                        KeepHigher(software, entry.SoftwareId, entry);
                    }
                    break;
            }
        }

        return new([.. processes], software, kinds);
    }

    public OptimizationProtectionMatch Resolve(
        string? softwareId,
        string? softwareKind,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> executablePaths,
        IReadOnlyList<string>? hostedServiceNames = null)
    {
        ProtectedOptimizationTarget? byName = null;
        ProtectedOptimizationTarget? byPath = null;
        foreach (var entry in processEntries)
        {
            if (!SoftwareProcessMatch.Matches(entry.ProcessName, entry.ExecutablePath, processNames, executablePaths))
            {
                continue;
            }

            if (entry.ExecutablePath is null)
            {
                byName = Higher(byName, entry.Target);
            }
            else
            {
                byPath = Higher(byPath, entry.Target);
            }
        }

        if ((byPath ?? byName) is { } process)
        {
            return OptimizationProtectionMatch.From(process);
        }

        if (!string.IsNullOrWhiteSpace(softwareId) && softwareEntries.TryGetValue(softwareId, out var own))
        {
            return OptimizationProtectionMatch.From(own);
        }

        ProtectedOptimizationTarget? hosted = null;
        foreach (var serviceName in hostedServiceNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(serviceName)
                && softwareEntries.TryGetValue(WindowsServiceSoftwareIds.ForService(serviceName), out var service))
            {
                hosted = Higher(hosted, service);
            }
        }

        if (hosted is not null)
        {
            return OptimizationProtectionMatch.From(hosted);
        }

        return !string.IsNullOrWhiteSpace(softwareKind) && kindEntries.TryGetValue(softwareKind, out var kind)
            ? OptimizationProtectionMatch.From(kind)
            : OptimizationProtectionMatch.None;
    }

    /// <summary>
    /// Software that reports leave alone, as before 10-29: every active software entry that protects (a user
    /// entry at 0, which switched a default off, does not).
    /// </summary>
    public static IReadOnlySet<string> SoftwareProtectedFromReports(IReadOnlyList<ProtectedOptimizationTarget> entries)
        => entries
            .Where(static item => item.State == OptimizationProtectionStates.Active
                && item.TargetType == OptimizationProtectionTargetTypes.Software
                && !string.IsNullOrWhiteSpace(item.SoftwareId)
                && OptimizationProtectionLevels.Normalize(item.ProtectionLevel) > OptimizationProtectionLevels.None)
            .Select(static item => item.SoftwareId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void KeepHigher(
        Dictionary<string, ProtectedOptimizationTarget> entries,
        string key,
        ProtectedOptimizationTarget entry)
    {
        entries[key] = entries.TryGetValue(key, out var existing) ? Higher(existing, entry)! : entry;
    }

    private static ProtectedOptimizationTarget? Higher(ProtectedOptimizationTarget? current, ProtectedOptimizationTarget next)
    {
        if (current is null)
        {
            return next;
        }

        var currentLevel = OptimizationProtectionLevels.Normalize(current.ProtectionLevel);
        var nextLevel = OptimizationProtectionLevels.Normalize(next.ProtectionLevel);
        return nextLevel > currentLevel
            || (nextLevel == currentLevel && string.CompareOrdinal(next.Id, current.Id) < 0)
            ? next
            : current;
    }

    private sealed record ProcessEntry(ProtectedOptimizationTarget Target, string ProcessName, string? ExecutablePath);
}

/// <summary>The protection one process resolves to, and the entry that gave it (null when none matched).</summary>
public sealed record OptimizationProtectionMatch(
    int ProtectionLevel,
    string? ProtectionId,
    string? ProtectionSource)
{
    public static OptimizationProtectionMatch None { get; } =
        new(OptimizationProtectionLevels.None, null, null);

    internal static OptimizationProtectionMatch From(ProtectedOptimizationTarget entry)
        => new(
            OptimizationProtectionLevels.Normalize(entry.ProtectionLevel),
            entry.Id,
            entry.ProtectionSource);
}
