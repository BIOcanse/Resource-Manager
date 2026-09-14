using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class OptimizationProtectionService(
    IOptimizationProtectionStore store,
    ISoftwareRegistryView softwareRegistry) : IOptimizationProtectionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ProtectedOptimizationTarget[] targets = [];
    private bool loaded;

    public int ProtectedTargetCount => Volatile.Read(ref targets).Length;

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
                CreateStableId(
                    $"protect|{report.Target.TargetType}|{report.Target.TargetKey}"),
                report.Target.TargetType,
                report.Target.TargetKey,
                report.Target.DisplayName,
                report.Target.SoftwareId,
                report.Target.SoftwareName,
                report.Target.SoftwareKind,
                now,
                "用户从性能优化报告中设为保护级。",
                report.Id,
                now,
                OptimizationProtectionStates.Active,
                AllowsPlacementAvoidance: true,
                ProtectionLevel: OptimizationProtectionLevels.Level2NoOptimization);
            ProtectedOptimizationTarget[] next =
            [
                .. targets
                    .Where(existing => !existing.Id.Equals(
                        item.Id,
                        StringComparison.OrdinalIgnoreCase)),
                item
            ];
            Array.Sort(next, static (left, right) =>
                left.ProtectedAt.CompareTo(right.ProtectedAt));
            await store.SaveAsync(next, cancellationToken);
            Volatile.Write(ref targets, next);
            return item;
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
            var next = targets
                .Select(item => item with
                {
                    LastVerifiedAt = now,
                    State = IsPresent(item, softwareIds, softwareNames)
                        ? OptimizationProtectionStates.Active
                        : OptimizationProtectionStates.Missing
                })
                .OrderBy(static item => item.ProtectedAt)
                .ToArray();
            await store.SaveAsync(next, cancellationToken);
            Volatile.Write(ref targets, next);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveProtectedTargetAsync(
        string protectionId,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = targets
                .Where(item => !item.Id.Equals(
                    protectionId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (next.Length == targets.Length)
            {
                return false;
            }
            await store.SaveAsync(next, cancellationToken);
            Volatile.Write(ref targets, next);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
        string protectionId,
        int protectionLevel,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var index = Array.FindIndex(
                targets,
                item => item.Id.Equals(
                    protectionId,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            var next = (ProtectedOptimizationTarget[])targets.Clone();
            next[index] = next[index] with
            {
                ProtectionLevel = OptimizationProtectionLevels.Normalize(protectionLevel),
                LastVerifiedAt = DateTimeOffset.UtcNow
            };
            Array.Sort(next, static (left, right) =>
                left.ProtectedAt.CompareTo(right.ProtectedAt));
            await store.SaveAsync(next, cancellationToken);
            Volatile.Write(ref targets, next);
            return next.First(item => item.Id.Equals(
                protectionId,
                StringComparison.OrdinalIgnoreCase));
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
            var next = persisted
                .OrderBy(static item => item.ProtectedAt)
                .ToArray();
            Volatile.Write(ref targets, next);
            Volatile.Write(ref loaded, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsPresent(
        ProtectedOptimizationTarget item,
        IReadOnlySet<string> softwareIds,
        IReadOnlySet<string> softwareNames)
    {
        return (!string.IsNullOrWhiteSpace(item.SoftwareId)
                && softwareIds.Contains(item.SoftwareId))
            || softwareNames.Contains(NormalizeTextKey(
                item.SoftwareName ?? item.DisplayName));
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

    private static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
