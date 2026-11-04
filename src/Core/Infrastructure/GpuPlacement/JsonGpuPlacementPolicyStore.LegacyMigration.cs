using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

// Compatibility boundary for policy files written before 2.0.0. The old GPU mode fields are
// folded into GpuSchedulingExcluded / RuntimeHotSwitchEnabled here and nowhere else; the
// application only ever sees 2.0.0 documents. Delete this file once no 1.x files remain to
// migrate (no reverse compatibility: an old binary reading 2.0.0 sees every software as
// GPU-disabled, which is called out in the release notes).
public sealed partial class JsonGpuPlacementPolicyStore
{
    private const string LegacyModeInherit = "Inherit";
    private const string LegacyModeDisabled = "Disabled";
    private const string LegacyModePreview = "Preview";
    private const string LegacyModeAuto = "Auto";
    private const string LegacyModeManual = "Manual";
    private const string LegacySchedulingOrdinary = "Ordinary";

    // A file that is not a JSON document keeps failing with JsonException, as before the
    // migration existed; the store never replaces it.
    private static string? ReadDocumentVersion(byte[] content)
    {
        var probe = JsonSerializer.Deserialize<DocumentVersionProbe>(content, JsonOptions);
        if (probe is null) throw new InvalidDataException("The GPU placement policy file is empty.");
        return probe.Version;
    }

    private static bool IsLegacyDocumentVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            || version.Trim().StartsWith("1.", StringComparison.Ordinal);
    }

    private GpuPlacementPolicyDocument MigrateLegacyDocument(byte[] content)
    {
        LegacyDocument? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyDocument>(content, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The legacy GPU placement policy file is invalid.", exception);
        }

        if (legacy is null) throw new InvalidDataException("The legacy GPU placement policy file is empty.");

        var softwarePolicies = new List<GpuPlacementSoftwarePolicy>();
        var softwareById = new Dictionary<string, (GpuPlacementSoftwarePolicy Policy, bool SchedulingPrecise)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in legacy.SoftwarePolicies ?? [])
        {
            if (item is null) continue;
            var schedulingPrecise = !IsLegacyOrdinary(item.SchedulingMode);
            var policy = new GpuPlacementSoftwarePolicy(
                item.SoftwareId ?? "",
                item.SoftwareName ?? "",
                item.AllowedProviders ?? GpuPlacementProviderIds.Defaults,
                item.StartupTargetGpu ?? GpuPlacementTargets.SystemDefaultGpu,
                // Ordinary runtime scheduling had no path other than "do not switch while running".
                schedulingPrecise && IsLegacyOrdinary(item.RuntimeSchedulingMode) ? false : item.RuntimeHotSwitchEnabled,
                item.PreferredRuntimeSwitchMethod ?? GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
                item.BaseScoreOverride,
                item.ProcessOverrideAllowed,
                item.UpdatedAt,
                CpuSchedulingExcluded: false,
                MemorySchedulingExcluded: false,
                GpuSchedulingExcluded: !schedulingPrecise || !LegacySoftwareModeParticipates(item.EnabledMode),
                item.CpuMaximumOccupancyMode ?? CpuMaximumOccupancyModes.AllCores,
                item.CpuManualExclusivePositionIds ?? [],
                item.CpuManualLockedPositionIds ?? [],
                item.HostMemoryProtectionNoDump,
                item.HostMemoryProtectionNeverTerminate);
            softwarePolicies.Add(policy);
            var key = policy.SoftwareId.Trim();
            if (key.Length > 0
                && (!softwareById.TryGetValue(key, out var existing) || existing.Policy.UpdatedAt < policy.UpdatedAt))
            {
                softwareById[key] = (policy, schedulingPrecise);
            }
        }

        var processPolicies = new List<GpuPlacementProcessPolicy>();
        foreach (var item in legacy.ProcessPolicies ?? [])
        {
            if (item is null) continue;
            WarnWhenProcessGpuOverrideIsDropped(item, softwareById);
            processPolicies.Add(new GpuPlacementProcessPolicy(
                item.SoftwareId ?? "",
                item.ProcessKey ?? "",
                item.ProcessName ?? "",
                item.ExecutablePath,
                item.Inherit,
                item.AllowedProviders ?? GpuPlacementProviderIds.Defaults,
                item.BaseScoreOverride,
                item.UpdatedAt,
                item.StartupInterceptionEnabled));
        }

        logger.LogInformation(
            "Migrated the GPU placement policy file from version {LegacyVersion} to {Version}: {SoftwareCount} software and {ProcessCount} process policies.",
            legacy.Version ?? "(missing)",
            GpuPlacementPolicyDocumentVersions.Current,
            softwarePolicies.Count,
            processPolicies.Count);
        return new GpuPlacementPolicyDocument(
            GpuPlacementPolicyDocumentVersions.Current,
            softwarePolicies,
            processPolicies,
            legacy.UpdatedAt);
    }

    // The only lossy step: a process-level GPU mode that made the process participate
    // differently from its software is dropped, because participation is software-level now.
    private void WarnWhenProcessGpuOverrideIsDropped(
        LegacyProcessPolicy item,
        IReadOnlyDictionary<string, (GpuPlacementSoftwarePolicy Policy, bool SchedulingPrecise)> softwareById)
    {
        var softwareId = item.SoftwareId?.Trim();
        if (item.Inherit
            || string.IsNullOrEmpty(softwareId)
            || !softwareById.TryGetValue(softwareId, out var software)
            || !software.Policy.ProcessOverrideAllowed)
        {
            return;
        }

        // Old process modes: a missing or unknown value normalized to Inherit (follow the software).
        var processMode = item.EnabledMode?.Trim();
        var processParticipates = processMode switch
        {
            LegacyModeDisabled or LegacyModePreview => false,
            LegacyModeAuto or LegacyModeManual => software.SchedulingPrecise,
            _ => !software.Policy.GpuSchedulingExcluded
        };
        if (processParticipates != !software.Policy.GpuSchedulingExcluded)
        {
            logger.LogWarning(
                "Dropped the process-level GPU mode {EnabledMode} of {SoftwareId}/{ProcessKey} while migrating the GPU placement policy file; GPU scheduling participation now follows the software setting ({SoftwareParticipates}).",
                processMode,
                softwareId,
                item.ProcessKey,
                !software.Policy.GpuSchedulingExcluded);
        }
    }

    // Old software modes: Inherit resolved to Auto; Manual behaved exactly like Auto; a missing
    // or unknown value normalized to Preview, which never executed.
    private static bool LegacySoftwareModeParticipates(string? enabledMode)
    {
        var mode = enabledMode?.Trim();
        return mode == LegacyModeAuto || mode == LegacyModeManual || mode == LegacyModeInherit;
    }

    private static bool IsLegacyOrdinary(string? schedulingMode)
        => schedulingMode?.Trim() == LegacySchedulingOrdinary;

    private sealed record DocumentVersionProbe(string? Version);

    private sealed record LegacyDocument(
        string? Version,
        IReadOnlyList<LegacySoftwarePolicy?>? SoftwarePolicies,
        IReadOnlyList<LegacyProcessPolicy?>? ProcessPolicies,
        DateTimeOffset UpdatedAt);

    private sealed record LegacySoftwarePolicy(
        string? SoftwareId,
        string? SoftwareName,
        string? EnabledMode,
        IReadOnlyList<string>? AllowedProviders,
        string? SchedulingMode,
        string? StartupTargetGpu,
        string? RuntimeSchedulingMode,
        bool? RuntimeHotSwitchEnabled,
        string? PreferredRuntimeSwitchMethod,
        double? BaseScoreOverride,
        bool ProcessOverrideAllowed,
        DateTimeOffset UpdatedAt,
        string? CpuMaximumOccupancyMode,
        IReadOnlyList<string>? CpuManualExclusivePositionIds,
        IReadOnlyList<string>? CpuManualLockedPositionIds,
        bool HostMemoryProtectionNoDump = false,
        bool HostMemoryProtectionNeverTerminate = false);

    private sealed record LegacyProcessPolicy(
        string? SoftwareId,
        string? ProcessKey,
        string? ProcessName,
        string? ExecutablePath,
        bool Inherit,
        string? EnabledMode,
        IReadOnlyList<string>? AllowedProviders,
        double? BaseScoreOverride,
        DateTimeOffset UpdatedAt,
        bool StartupInterceptionEnabled);
}
