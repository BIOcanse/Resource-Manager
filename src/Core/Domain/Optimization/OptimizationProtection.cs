namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationProtectionStates
{
    public const string Active = "active";
    public const string Missing = "missing";
    public const string Stale = "stale";
}

/// <summary>
/// Protection is one kind: no freeze (L4). "Never optimized" is not a protection; it is the high base-score tier
/// (performance_importance_scoring_model.md §5), so a target that must not be suppressed gets a high base score.
/// </summary>
public static class OptimizationProtectionLevels
{
    /// <summary>No protection. A user entry at 0 is the user switching the default of that target off.</summary>
    public const int None = 0;
    public const int NoFreeze = 1;
    public const int Default = NoFreeze;

    /// <summary>
    /// 0 stays 0; every other value reads as no-freeze, including the level 2 that software detail and reports
    /// stored before 10-29 (user ruling 「取消2级保护机制」「只需要留一级保护」).
    /// </summary>
    public static int Normalize(int value) => value == None ? None : NoFreeze;

    public static bool IsValid(int value) => value is None or NoFreeze;
}

/// <summary>Where a protection entry comes from. The first three are the user's; the last two are defaults.</summary>
public static class OptimizationProtectionSources
{
    public const string SoftwareDetail = "software_detail";
    public const string Report = "report";
    /// <summary>The user changed the level of an entry in the protection list.</summary>
    public const string ProtectionList = "protection_list";
    /// <summary>Default from a software catalog group marked <c>noFreeze</c> (by software identity, service or process name).</summary>
    public const string SoftwareCatalog = "software_catalog";
    /// <summary>Default no-freeze of every Windows system, component and service process.</summary>
    public const string SystemDefault = "system_default";

    public static bool IsDefault(string source)
        => source is SoftwareCatalog or SystemDefault;
}

/// <summary>What a protection entry's <see cref="ProtectedOptimizationTarget.TargetKey"/> names.</summary>
public static class OptimizationProtectionTargetTypes
{
    /// <summary>One software identity: key <c>software:&lt;software id&gt;</c>.</summary>
    public const string Software = OptimizationReportTargetTypes.Software;
    /// <summary>
    /// Every process with this image name (and path): key <c>process-name:&lt;name&gt;</c> or
    /// <c>process-path:&lt;path&gt;</c>; the matcher is <see cref="ProtectedOptimizationTarget.ProcessName"/>
    /// and <see cref="ProtectedOptimizationTarget.ExecutablePath"/>.
    /// </summary>
    public const string Process = "process";
    /// <summary>Every process attributed to software of one kind: key <c>software-kind:&lt;kind&gt;</c>.</summary>
    public const string SoftwareKind = "software_kind";
}

/// <summary>One protection entry; <see cref="ProtectionLevel"/> is 0 (none) or 1 (no freeze).</summary>
public sealed record ProtectedOptimizationTarget(
    string Id,
    string TargetType,
    string TargetKey,
    string? SoftwareId,
    string? SoftwareName,
    string? SoftwareKind,
    DateTimeOffset ProtectedAt,
    string ProtectionSource,
    string CreatedFromReportId,
    DateTimeOffset? LastVerifiedAt,
    string State,
    bool AllowsPlacementAvoidance,
    int ProtectionLevel = OptimizationProtectionLevels.Default)
{
    /// <summary>On a user entry that replaces a default: the default's level; null otherwise.</summary>
    public int? DefaultProtectionLevel { get; init; }

    /// <summary>On a user entry that replaces a default: the default's source; null otherwise.</summary>
    public string? DefaultProtectionSource { get; init; }

    /// <summary>The software catalog group that gives this entry (or the default it replaces).</summary>
    public string? PolicyGroupId { get; init; }

    /// <summary>Process entries: the image name the entry matches.</summary>
    public string? ProcessName { get; init; }

    /// <summary>Process entries: the full executable path the entry is limited to, as the catalog writes it.</summary>
    public string? ExecutablePath { get; init; }
}

public sealed record OptimizationProtectionLevelRequest(
    int ProtectionLevel);

public sealed record SoftwareProtectionRequest(bool Enabled);
