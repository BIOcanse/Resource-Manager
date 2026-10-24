using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class JsonGpuPlacementPolicyStore(
    IHostEnvironment environment,
    ILogger<JsonGpuPlacementPolicyStore> logger) : IGpuPlacementPolicyStore
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

    // Runs inside the gate. A 2.0.0 file is read as is unless it contains old wire spellings,
    // which are atomically rewritten; a missing or 1.x version is migrated before it is returned.
    private async Task<GpuPlacementPolicyDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(storagePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return GpuPlacementPolicyDocument.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return GpuPlacementPolicyDocument.Empty;
        }

        var version = ReadDocumentVersion(content);
        if (string.Equals(version, GpuPlacementPolicyDocumentVersions.Current, StringComparison.Ordinal))
        {
            var document = JsonSerializer.Deserialize<GpuPlacementPolicyDocument>(content, JsonOptions);
            if (document is null) throw new InvalidDataException("The GPU placement policy file is empty.");
            var normalized = ReadStoredDocument(document);
            if (HasLegacyWireValues(document))
            {
                await SaveCoreAsync(normalized, cancellationToken);
            }
            return normalized;
        }

        if (!IsLegacyDocumentVersion(version))
        {
            throw new InvalidDataException(
                $"The GPU placement policy file version '{version}' is newer than this application supports.");
        }

        var migrated = ReadStoredDocument(MigrateLegacyDocument(content));
        await SaveCoreAsync(migrated, cancellationToken);
        return migrated;
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

        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, NormalizeDocument(document), JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, storagePath);
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
        if (throwOnInvalid)
        {
            ValidateRequestValue(GpuPlacementTargets.IsKnown(policy.StartupTargetGpu),
                "GPU 启动目标必须使用受支持的值。", policy.StartupTargetGpu);
            ValidateRequestValue(GpuPlacementRuntimeSwitchMethods.IsKnown(policy.PreferredRuntimeSwitchMethod),
                "GPU 运行时切换方式必须使用受支持的值。", policy.PreferredRuntimeSwitchMethod);
            ValidateRequestValue(CpuMaximumOccupancyModes.IsKnown(policy.CpuMaximumOccupancyMode),
                "CPU 最大占用模式必须使用受支持的值。", policy.CpuMaximumOccupancyMode);
        }
        return new GpuPlacementSoftwarePolicy(
            softwareId,
            softwareName,
            NormalizeProviderIds(policy.AllowedProviders),
            GpuPlacementTargets.Normalize(policy.StartupTargetGpu),
            policy.RuntimeHotSwitchEnabled,
            GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            NormalizeBaseScoreOverride(policy.BaseScoreOverride),
            policy.ProcessOverrideAllowed,
            updatedAt,
            policy.CpuSchedulingExcluded,
            policy.MemorySchedulingExcluded,
            policy.GpuSchedulingExcluded,
            CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode),
            NormalizeCpuPositionIds(policy.CpuManualExclusivePositionIds),
            NormalizeCpuPositionIds(policy.CpuManualLockedPositionIds));
    }

    // Keep legacy-token compatibility at the persistence read boundary. SaveCoreAsync uses
    // NormalizeDocument directly and therefore never accepts old stored spellings.
    private static GpuPlacementPolicyDocument ReadStoredDocument(GpuPlacementPolicyDocument? document)
    {
        if (document is null)
        {
            return GpuPlacementPolicyDocument.Empty;
        }

        var softwarePolicies = (document.SoftwarePolicies ?? [])
            .Select(static policy => ReadStoredSoftwarePolicy(policy))
            .ToArray();
        return NormalizeDocument(document with { SoftwarePolicies = softwarePolicies });
    }

    private static GpuPlacementSoftwarePolicy ReadStoredSoftwarePolicy(GpuPlacementSoftwarePolicy? policy)
    {
        if (policy is null)
        {
            return GpuPlacementPolicyDefaults.CreateSoftwarePolicy("", "");
        }

        return policy with
        {
            StartupTargetGpu = GpuPlacementTargets.ReadStored(policy.StartupTargetGpu),
            PreferredRuntimeSwitchMethod = GpuPlacementRuntimeSwitchMethods.ReadStored(policy.PreferredRuntimeSwitchMethod),
            CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.ReadStored(policy.CpuMaximumOccupancyMode)
        };
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

            return new GpuPlacementProcessPolicy("", "", "", null, true, GpuPlacementProviderIds.Defaults, null, DateTimeOffset.MinValue);
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
            NormalizeProviderIds(policy.AllowedProviders),
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
            StartupTargetGpu = GpuPlacementTargets.Normalize(policy.StartupTargetGpu),
            RuntimeHotSwitchEnabled = policy.RuntimeHotSwitchEnabled
                ?? GpuPlacementPolicyDefaults.ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            PreferredRuntimeSwitchMethod = GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode),
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

    private static void ValidateRequestValue(bool isKnown, string message, string? value)
    {
        if (!isKnown)
        {
            throw new InvalidOperationException($"{message} Unsupported value '{value}'.");
        }
    }

    private static bool HasLegacyWireValues(GpuPlacementPolicyDocument document)
    {
        return (document.SoftwarePolicies ?? []).Any(policy => policy is not null &&
            (!string.Equals(policy.StartupTargetGpu,
                GpuPlacementTargets.ReadStored(policy.StartupTargetGpu), StringComparison.Ordinal)
            || !string.Equals(policy.PreferredRuntimeSwitchMethod,
                GpuPlacementRuntimeSwitchMethods.ReadStored(policy.PreferredRuntimeSwitchMethod), StringComparison.Ordinal)
            || !string.Equals(policy.CpuMaximumOccupancyMode,
                CpuMaximumOccupancyModes.ReadStored(policy.CpuMaximumOccupancyMode), StringComparison.Ordinal)));
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
