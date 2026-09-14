using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class JsonGpuPlacementPolicyStore(IHostEnvironment environment) : IGpuPlacementPolicyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData",
        "SoftwareProfiles",
        "gpu-placement-policies.local.json");

    public async Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(
        string softwareId,
        string softwareName,
        string? softwareKind,
        CancellationToken cancellationToken)
    {
        var normalizedId = RequireText(softwareId, "软件标识不能为空。");
        var normalizedName = CleanText(softwareName) ?? normalizedId;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var policy = document.SoftwarePolicies.FirstOrDefault(policy =>
                    policy.SoftwareId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                ?? GpuPlacementPolicyDefaults.CreateSoftwarePolicy(normalizedId, normalizedName, softwareKind);
            return ApplySoftwareKindDefaults(policy, softwareKind);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(
        GpuPlacementSoftwarePolicy policy,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeSoftwarePolicy(policy, DateTimeOffset.Now, true);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var policies = document.SoftwarePolicies
                .Where(existing => !existing.SoftwareId.Equals(normalized.SoftwareId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderBy(static item => item.SoftwareName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var next = document with
            {
                Version = GpuPlacementPolicyDocumentVersions.Current,
                SoftwarePolicies = policies,
                UpdatedAt = normalized.UpdatedAt
            };
            await SaveCoreAsync(next, cancellationToken);
            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(
        GpuPlacementProcessPolicy policy,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeProcessPolicy(policy, DateTimeOffset.Now, true);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var policies = document.ProcessPolicies
                .Where(existing => !IsSameProcessPolicy(existing, normalized))
                .Append(normalized)
                .OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.ProcessKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var next = document with
            {
                Version = GpuPlacementPolicyDocumentVersions.Current,
                ProcessPolicies = policies,
                UpdatedAt = normalized.UpdatedAt
            };
            await SaveCoreAsync(next, cancellationToken);
            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GpuPlacementPolicyDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storagePath))
        {
            return GpuPlacementPolicyDocument.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(storagePath);
            var document = await JsonSerializer.DeserializeAsync<GpuPlacementPolicyDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return NormalizeDocument(document);
        }
        catch (JsonException)
        {
            return GpuPlacementPolicyDocument.Empty;
        }
        catch (IOException)
        {
            return GpuPlacementPolicyDocument.Empty;
        }
    }

    private async Task SaveCoreAsync(
        GpuPlacementPolicyDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(storagePath);
        await JsonSerializer.SerializeAsync(stream, NormalizeDocument(document), JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static GpuPlacementPolicyDocument NormalizeDocument(GpuPlacementPolicyDocument? document)
    {
        if (document is null)
        {
            return GpuPlacementPolicyDocument.Empty;
        }

        var softwarePolicies = (document.SoftwarePolicies ?? [])
            .Select(static policy => NormalizeSoftwarePolicy(policy, policy.UpdatedAt, false))
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static policy => policy.UpdatedAt).First())
            .OrderBy(static policy => policy.SoftwareName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var processPolicies = (document.ProcessPolicies ?? [])
            .Select(static policy => NormalizeProcessPolicy(policy, policy.UpdatedAt, false))
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId)
                && !string.IsNullOrWhiteSpace(policy.ProcessKey))
            .GroupBy(static policy => $"{policy.SoftwareId}\n{policy.ProcessKey}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static policy => policy.UpdatedAt).First())
            .OrderBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.ProcessKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GpuPlacementPolicyDocument(
            GpuPlacementPolicyDocumentVersions.Current,
            softwarePolicies,
            processPolicies,
            document.UpdatedAt);
    }

    private static GpuPlacementSoftwarePolicy NormalizeSoftwarePolicy(
        GpuPlacementSoftwarePolicy? policy,
        DateTimeOffset updatedAt,
        bool throwOnInvalid)
    {
        if (policy is null)
        {
            if (throwOnInvalid)
            {
                throw new InvalidOperationException("GPU 调度软件策略不能为空。");
            }

            return GpuPlacementPolicyDefaults.CreateSoftwarePolicy("", "");
        }

        var softwareId = throwOnInvalid
            ? RequireText(policy.SoftwareId, "软件标识不能为空。")
            : CleanText(policy.SoftwareId) ?? "";
        var softwareName = CleanText(policy.SoftwareName) ?? softwareId;
        var schedulingMode = GpuPlacementSchedulingModes.Normalize(policy.SchedulingMode);
        var runtimeSchedulingMode = schedulingMode.Equals(GpuPlacementSchedulingModes.Ordinary, StringComparison.OrdinalIgnoreCase)
            ? GpuPlacementRuntimeSchedulingModes.Ordinary
            : GpuPlacementRuntimeSchedulingModes.Normalize(policy.RuntimeSchedulingMode);
        var startupTargetGpu = GpuPlacementTargets.Normalize(policy.StartupTargetGpu);
        var targetGpu = GpuPlacementTargets.Normalize(policy.TargetGpu);
        if (schedulingMode.Equals(GpuPlacementSchedulingModes.Ordinary, StringComparison.OrdinalIgnoreCase))
        {
            startupTargetGpu = GpuPlacementTargets.NormalizeSystemTarget(startupTargetGpu);
            targetGpu = GpuPlacementTargets.NormalizeSystemTarget(targetGpu);
        }
        else if (runtimeSchedulingMode.Equals(GpuPlacementRuntimeSchedulingModes.Ordinary, StringComparison.OrdinalIgnoreCase))
        {
            targetGpu = GpuPlacementTargets.NormalizeSystemTarget(targetGpu);
        }
        var cpuMaximumOccupancyMode = CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode);

        return new GpuPlacementSoftwarePolicy(
            softwareId,
            softwareName,
            GpuPlacementPolicyModes.Normalize(policy.EnabledMode),
            GpuPlacementRiskLevels.Normalize(policy.MaxRisk),
            NormalizeProviderIds(policy.AllowedProviders),
            schedulingMode,
            startupTargetGpu,
            targetGpu,
            runtimeSchedulingMode,
            GpuPlacementExplicitSelectionModes.Normalize(policy.ExplicitSelectionMode),
            policy.RuntimeHotSwitchEnabled,
            GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            NormalizeBaseScoreOverride(policy.BaseScoreOverride),
            policy.ProcessOverrideAllowed,
            updatedAt,
            cpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.SingleCcd, StringComparison.OrdinalIgnoreCase),
            policy.GpuExclusive,
            policy.AbsolutePerformanceModeEnabled,
            cpuMaximumOccupancyMode,
            policy.CpuExclusiveLocksAffinity,
            NormalizeCpuPositionIds(policy.CpuManualExclusivePositionIds),
            NormalizeCpuPositionIds(policy.CpuManualLockedPositionIds));
    }

    private static GpuPlacementProcessPolicy NormalizeProcessPolicy(
        GpuPlacementProcessPolicy? policy,
        DateTimeOffset updatedAt,
        bool throwOnInvalid)
    {
        if (policy is null)
        {
            if (throwOnInvalid)
            {
                throw new InvalidOperationException("GPU 调度进程策略不能为空。");
            }

            return new GpuPlacementProcessPolicy("", "", "", null, true, GpuPlacementPolicyModes.Inherit, GpuPlacementRiskLevels.Low, GpuPlacementProviderIds.Defaults, GpuPlacementTargets.SystemDefaultGpu, GpuPlacementExplicitSelectionModes.DefaultSkip, null, DateTimeOffset.MinValue);
        }

        var softwareId = throwOnInvalid
            ? RequireText(policy.SoftwareId, "软件标识不能为空。")
            : CleanText(policy.SoftwareId) ?? "";
        var processKey = throwOnInvalid
            ? RequireText(policy.ProcessKey, "进程标识不能为空。")
            : CleanText(policy.ProcessKey) ?? "";
        var processName = CleanText(policy.ProcessName) ?? processKey;
        return new GpuPlacementProcessPolicy(
            softwareId,
            processKey,
            processName,
            CleanText(policy.ExecutablePath),
            policy.Inherit,
            GpuPlacementPolicyModes.Normalize(policy.EnabledMode, GpuPlacementPolicyModes.Inherit),
            GpuPlacementRiskLevels.Normalize(policy.MaxRisk),
            NormalizeProviderIds(policy.AllowedProviders),
            GpuPlacementTargets.Normalize(policy.TargetGpu),
            GpuPlacementExplicitSelectionModes.Normalize(policy.ExplicitSelectionMode),
            policy.Inherit ? null : NormalizeBaseScoreOverride(policy.BaseScoreOverride),
            updatedAt,
            policy.StartupInterceptionEnabled);
    }

    private static IReadOnlyList<string> NormalizeProviderIds(IEnumerable<string>? providers)
    {
        var normalized = (providers ?? GpuPlacementProviderIds.Defaults)
            .Select(static provider => CleanText(provider))
            .Where(static provider => !string.IsNullOrWhiteSpace(provider))
            .Select(static provider => provider!)
            .Where(static provider => GpuPlacementProviderIds.Known.Contains(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static provider => ProviderSort(provider))
            .ThenBy(static provider => provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized;
    }

    private static int ProviderSort(string provider)
    {
        return provider switch
        {
            GpuPlacementProviderIds.WindowsGraphicsPreference => 0,
            GpuPlacementProviderIds.VendorProfile => 1,
            GpuPlacementProviderIds.DxgiLaunchShim => 2,
            GpuPlacementProviderIds.D3dDeviceCreateShim => 3,
            GpuPlacementProviderIds.VulkanImplicitLayer => 4,
            GpuPlacementProviderIds.VulkanExplicitLayer => 4,
            GpuPlacementProviderIds.ComputeVisibility => 5,
            _ => 100
        };
    }

    private static double? NormalizeBaseScoreOverride(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Round(Math.Clamp(value.Value, 0, 100), 2);
    }

    private static GpuPlacementSoftwarePolicy ApplySoftwareKindDefaults(
        GpuPlacementSoftwarePolicy policy,
        string? softwareKind)
    {
        return policy with
        {
            SchedulingMode = GpuPlacementSchedulingModes.Normalize(policy.SchedulingMode),
            StartupTargetGpu = GpuPlacementTargets.Normalize(policy.StartupTargetGpu),
            TargetGpu = GpuPlacementTargets.Normalize(policy.TargetGpu),
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Normalize(policy.RuntimeSchedulingMode),
            RuntimeHotSwitchEnabled = policy.RuntimeHotSwitchEnabled
                ?? GpuPlacementPolicyDefaults.ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            PreferredRuntimeSwitchMethod = GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode),
            KeepCpuProcessesOnMainCcd = CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode)
                .Equals(CpuMaximumOccupancyModes.SingleCcd, StringComparison.OrdinalIgnoreCase),
            CpuManualExclusivePositionIds = NormalizeCpuPositionIds(policy.CpuManualExclusivePositionIds),
            CpuManualLockedPositionIds = NormalizeCpuPositionIds(policy.CpuManualLockedPositionIds)
        };
    }

    private static IReadOnlyList<string> NormalizeCpuPositionIds(IEnumerable<string>? positionIds)
    {
        return (positionIds ?? [])
            .Select(static id => CleanText(id))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSameProcessPolicy(
        GpuPlacementProcessPolicy left,
        GpuPlacementProcessPolicy right)
    {
        return left.SoftwareId.Equals(right.SoftwareId, StringComparison.OrdinalIgnoreCase)
            && left.ProcessKey.Equals(right.ProcessKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireText(string? value, string message)
    {
        return CleanText(value) ?? throw new InvalidOperationException(message);
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
