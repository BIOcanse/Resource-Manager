using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

/// <summary>
/// Owner of the protection list. The list is the default entries (software catalog, system default) with the
/// user's entries in place of the defaults they change; only the user's entries are stored. Every read returns the
/// same immutable list, rebuilt after each user change.
/// </summary>
public sealed class OptimizationProtectionService(
    IOptimizationProtectionStore store,
    ISoftwareRegistryView softwareRegistry,
    ISoftwarePolicyProfileProvider profileProvider) : IOptimizationProtectionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ProtectedOptimizationTarget[] defaults = [];
    private ProtectedOptimizationTarget[] userTargets = [];
    private ProtectedOptimizationTarget[] targets = [];
    private bool loaded;

    public int ProtectedTargetCount => Volatile.Read(ref targets).Length;

    /// <summary>
    /// Software detail protection switch. On: a user entry at no-freeze. Off: a stored user entry at 0 when the
    /// software has a default (catalog) protection, so the default stays switched off; otherwise the user entry is
    /// removed. Returns the user entry now in the list, or null when there is none.
    /// </summary>
    public async Task<ProtectedOptimizationTarget?> SetSoftwareProtectionAsync(string softwareId, bool enabled,
        CancellationToken cancellationToken)
    {
        var software = (await softwareRegistry.GetSoftwareAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.Id, softwareId, StringComparison.OrdinalIgnoreCase));
        await EnsureLoadedAsync(cancellationToken);
        var softwareDefault = Volatile.Read(ref defaults).FirstOrDefault(item =>
            item.TargetType == OptimizationProtectionTargetTypes.Software
            && string.Equals(item.SoftwareId, softwareId, StringComparison.OrdinalIgnoreCase));
        if (software is null && softwareDefault is null)
        {
            throw new KeyNotFoundException("Unknown software identity.");
        }

        var id = software?.Id ?? softwareDefault!.SoftwareId!;
        var key = OptimizationProtectionDefaults.SoftwareKey(id);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = userTargets.Where(item => !string.Equals(item.SoftwareId, id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.TargetKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
            int? level = enabled
                ? OptimizationProtectionLevels.NoFreeze
                : softwareDefault is not null
                    ? OptimizationProtectionLevels.None
                    : null;
            ProtectedOptimizationTarget? protection = null;
            if (level is { } userLevel)
            {
                var now = DateTimeOffset.UtcNow;
                protection = new(OptimizationProtectionDefaults.CreateId(OptimizationProtectionTargetTypes.Software, key),
                    OptimizationProtectionTargetTypes.Software, key, id, software?.Name ?? softwareDefault!.SoftwareName,
                    software?.Kind ?? softwareDefault!.SoftwareKind, now,
                    OptimizationProtectionSources.SoftwareDetail, "software-detail", now, OptimizationProtectionStates.Active,
                    AllowsPlacementAvoidance: true, ProtectionLevel: userLevel);
                next.Add(protection);
            }

            var published = await CommitAsync(next, cancellationToken);
            return protection is null ? null : Find(published, protection.Id);
        }
        finally { gate.Release(); }
    }

    public async Task<ProtectedOptimizationTarget?> ProtectReportAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Target.TargetType != OptimizationReportTargetTypes.Software)
        {
            return null;
        }

        await EnsureLoadedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var item = new ProtectedOptimizationTarget(
                OptimizationProtectionDefaults.CreateId(report.Target.TargetType, report.Target.TargetKey),
                report.Target.TargetType,
                report.Target.TargetKey,
                report.Target.SoftwareId,
                report.Target.SoftwareName,
                report.Target.SoftwareKind,
                now,
                OptimizationProtectionSources.Report,
                report.Id,
                now,
                OptimizationProtectionStates.Active,
                AllowsPlacementAvoidance: true,
                ProtectionLevel: OptimizationProtectionLevels.NoFreeze);
            ProtectedOptimizationTarget[] next =
            [
                .. userTargets
                    .Where(existing => !existing.Id.Equals(
                        item.Id,
                        StringComparison.OrdinalIgnoreCase)),
                item
            ];
            return Find(await CommitAsync(next, cancellationToken), item.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return Volatile.Read(ref targets);
    }

    /// <summary>Marks the user's software entries active or missing by the current software list.</summary>
    public async Task<IReadOnlyList<ProtectedOptimizationTarget>> RefreshProtectedTargetsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var software = await softwareRegistry.GetSoftwareAsync(cancellationToken);
        var softwareIds = software
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var softwareNames = software
            .Select(static item => NormalizeTextKey(item.Name))
            .Where(static item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var next = userTargets
                .Select(item => item.TargetType != OptimizationProtectionTargetTypes.Software ? item : item with
                {
                    LastVerifiedAt = now,
                    State = IsPresent(item, softwareIds, softwareNames)
                        ? OptimizationProtectionStates.Active
                        : OptimizationProtectionStates.Missing
                })
                .ToArray();
            return await CommitAsync(next, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes the user's own entry. A default it replaced returns to the list; a default itself cannot be
    /// removed (set its level to 0 to switch it off), so this returns false for it.
    /// </summary>
    public async Task<bool> RemoveProtectedTargetAsync(
        string protectionId,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = userTargets
                .Where(item => !item.Id.Equals(
                    protectionId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (next.Length == userTargets.Length)
            {
                return false;
            }
            await CommitAsync(next, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sets the level (0 or 1) of one entry of the list. A user entry changes in place; a default gets a stored
    /// user entry for the same target (source <c>protection_list</c>), which replaces it. Null when no entry has
    /// this id.
    /// </summary>
    public async Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
        string protectionId,
        int protectionLevel,
        CancellationToken cancellationToken)
    {
        if (!OptimizationProtectionLevels.IsValid(protectionLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(protectionLevel), protectionLevel, "Protection level must be 0 or 1.");
        }

        await EnsureLoadedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var next = (ProtectedOptimizationTarget[])userTargets.Clone();
            var index = Array.FindIndex(
                next,
                item => item.Id.Equals(
                    protectionId,
                    StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                next[index] = next[index] with
                {
                    ProtectionLevel = protectionLevel,
                    LastVerifiedAt = now
                };
            }
            else if (defaults.FirstOrDefault(item => item.Id.Equals(protectionId, StringComparison.OrdinalIgnoreCase))
                is { } replacedDefault)
            {
                next = [.. next, OptimizationProtectionDefaults.CreateUserEntry(
                    replacedDefault,
                    protectionLevel,
                    OptimizationProtectionSources.ProtectionList,
                    OptimizationProtectionDefaults.ProtectionListOrigin,
                    now)];
            }
            else
            {
                return null;
            }

            return Find(await CommitAsync(next, cancellationToken), protectionId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> IsTargetProtectedAsync(
        OptimizationReportTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await EnsureLoadedAsync(cancellationToken);
        return Volatile.Read(ref targets).Any(item =>
            item.State == OptimizationProtectionStates.Active
            && item.ProtectionLevel > OptimizationProtectionLevels.None
            && item.TargetType.Equals(
                target.TargetType,
                StringComparison.OrdinalIgnoreCase)
            && (item.TargetKey.Equals(
                    target.TargetKey,
                    StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(target.SoftwareId)
                    && !string.IsNullOrWhiteSpace(item.SoftwareId)
                    && item.SoftwareId.Equals(
                        target.SoftwareId,
                        StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Saves the user entries, then publishes them and the rebuilt list. Called inside the gate.</summary>
    private async Task<ProtectedOptimizationTarget[]> CommitAsync(
        IEnumerable<ProtectedOptimizationTarget> nextUserTargets,
        CancellationToken cancellationToken)
    {
        var ordered = nextUserTargets.OrderBy(static item => item.ProtectedAt).ToArray();
        await store.SaveAsync(ordered, cancellationToken);
        var published = OptimizationProtectionDefaults.Merge(defaults, ordered);
        userTargets = ordered;
        Volatile.Write(ref targets, published);
        return published;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref loaded))
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (loaded)
            {
                return;
            }
            var persisted = await store.LoadAsync(cancellationToken);
            var profile = await profileProvider.GetProfileAsync(cancellationToken);
            var nextDefaults = OptimizationProtectionDefaults.Create(profile, DateTimeOffset.UtcNow);
            var nextUsers = persisted
                .OrderBy(static item => item.ProtectedAt)
                .ToArray();
            defaults = nextDefaults;
            userTargets = nextUsers;
            Volatile.Write(ref targets, OptimizationProtectionDefaults.Merge(nextDefaults, nextUsers));
            Volatile.Write(ref loaded, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ProtectedOptimizationTarget? Find(IReadOnlyList<ProtectedOptimizationTarget> list, string id)
        => list.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static bool IsPresent(
        ProtectedOptimizationTarget item,
        IReadOnlySet<string> softwareIds,
        IReadOnlySet<string> softwareNames)
    {
        return (!string.IsNullOrWhiteSpace(item.SoftwareId)
                && softwareIds.Contains(item.SoftwareId))
            || softwareNames.Contains(NormalizeTextKey(
                item.SoftwareName));
    }

    private static string NormalizeTextKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return new string(value
            .Trim()
            .Select(static value => char.IsLetterOrDigit(value)
                ? char.ToLowerInvariant(value)
                : '\0')
            .Where(static value => value != '\0')
            .ToArray());
    }
}
