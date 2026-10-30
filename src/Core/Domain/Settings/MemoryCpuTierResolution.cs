using System.Collections.Immutable;

namespace ResourceManager.App.Domain.Settings;

/// <summary>Wire names of the memory/CPU tier options (the frontend settings page keys its texts by them).
/// Each is the camelCase form of the option's freedom-point id: table memory_cpu_tiers, and for the stop
/// buffer and service identity cache the five-value points monitoring/build/simple/0 and /1.</summary>
public static class MemoryCpuTierOptionNames
{
    public const string GcGen0MaxBudget = "gcGen0MaxBudget";
    public const string GcConserveMemory = "gcConserveMemory";
    public const string LedgerMemoryModeFloor = "ledgerMemoryModeFloor";
    public const string BufferReturn = "bufferReturn";
    public const string NativeWorkareaGrowth = "nativeWorkareaGrowth";
    public const string SingleRequestStopBufferMs = "singleRequestStopBufferMs";
    public const string ServiceIdentityCacheMs = "serviceIdentityCacheMs";
    public const string BrowserRuntimeSnapshotSeconds = "browserRuntimeSnapshotSeconds";
    public const string PlatformSensorSnapshotSeconds = "platformSensorSnapshotSeconds";
    public const string StorageSnapshotSeconds = "storageSnapshotSeconds";
    public const string RendererAbsenceEntries = "rendererAbsenceEntries";
    public const string FileQuerySessionsMax = "fileQuerySessionsMax";
    public const string FileQuerySessionsPrebuilt = "fileQuerySessionsPrebuilt";
    public const string FileQueryIdleSessions = "fileQueryIdleSessions";
}

public static class MemoryCpuTierCategories
{
    public const string Proportional = "proportional";
    public const string Aggressive = "aggressive";
}

public static class MemoryCpuTierSources
{
    public const string Tier = "tier";
    public const string User = "user";
}

public static class MemoryCpuTierApplyModes
{
    public const string Immediate = "immediate";
    public const string Restart = "restart";
}

public static class MemoryCpuTierUnits
{
    public const string Bytes = "bytes";
    public const string Milliseconds = "milliseconds";
    public const string Seconds = "seconds";
    public const string Count = "count";
    public const string Level = "level";
}

public static class MemoryCpuTierRangeKinds
{
    public const string Integer = "integer";
    public const string Choice = "choice";
}

/// <summary>Choices of <see cref="MemoryCpuTierOptionNames.BufferReturn"/>: when the process-inventory
/// kernel-table workspace is returned.</summary>
public static class BufferReturnChoices
{
    /// <summary>Free the workspace after every parse.</summary>
    public const string ReleaseAfterUse = "releaseAfterUse";
    /// <summary>Shrink after 2, 8 or 32 consecutive low-use reads (each using at most a quarter).</summary>
    public const string ShrinkAfter2LowReads = "shrinkAfter2LowReads";
    public const string ShrinkAfter8LowReads = "shrinkAfter8LowReads";
    public const string ShrinkAfter32LowReads = "shrinkAfter32LowReads";
    /// <summary>Never shrink; after a release, the next read reserves the previous peak at once.</summary>
    public const string KeepPeak = "keepPeak";
}

/// <summary>Choices of <see cref="MemoryCpuTierOptionNames.LedgerMemoryModeFloor"/> (resolved and listed;
/// the Ledger owner reads it from step 2 of the memory/CPU tier design).</summary>
public static class LedgerMemoryModeFloorChoices
{
    public const string Optimize = "optimize";
    public const string AutoThrifty = "autoThrifty";
    public const string Auto = "auto";
    public const string AutoGenerous = "autoGenerous";
    public const string Unrestricted = "unrestricted";
}

/// <summary>Choices of <see cref="MemoryCpuTierOptionNames.NativeWorkareaGrowth"/> (resolved and listed;
/// the native owners read it from step 2 of the memory/CPU tier design).</summary>
public static class NativeWorkareaGrowthChoices
{
    public const string ExactRows = "exactRows";
    public const string Rows125 = "rows125";
    public const string Rows150 = "rows150";
    public const string Rows200 = "rows200";
    public const string ReserveCapacity = "reserveCapacity";
}

/// <summary>Choices of <see cref="MemoryCpuTierOptionNames.FileQueryIdleSessions"/>.</summary>
public static class FileQueryIdleSessionChoices
{
    /// <summary>A returned session that no waiter takes is closed.</summary>
    public const string Close = "close";
    /// <summary>A returned session stays in the pool for the next query.</summary>
    public const string Keep = "keep";
}

/// <summary>One option value: an integer, a choice, or not set (only where the option's range allows it).</summary>
public readonly record struct MemoryCpuTierValue(long? Integer, string? Choice)
{
    public bool IsUnset => Integer is null && Choice is null;
    public static MemoryCpuTierValue Unset => default;
    public static MemoryCpuTierValue Of(long value) => new(value, null);
    public static MemoryCpuTierValue Of(string choice) => new(null, choice);
}

/// <summary>An option's accepted values: an integer range (with <c>Unset</c> when "not set" is allowed)
/// or a fixed list of choices.</summary>
public sealed record MemoryCpuTierRange(string Kind, long Minimum, long Maximum, bool Unset, ImmutableArray<string> Choices)
{
    public static MemoryCpuTierRange Integer(long minimum, long maximum, bool unset = false)
        => new(MemoryCpuTierRangeKinds.Integer, minimum, maximum, unset, []);
    public static MemoryCpuTierRange Choice(params string[] choices)
        => new(MemoryCpuTierRangeKinds.Choice, 0, 0, false, [.. choices]);

    public bool Accepts(MemoryCpuTierValue value) => Kind == MemoryCpuTierRangeKinds.Integer
        ? value.IsUnset ? Unset : value.Choice is null && value.Integer >= Minimum && value.Integer <= Maximum
        : value.Integer is null && value.Choice is { } choice && Choices.Contains(choice, StringComparer.Ordinal);
}

/// <summary>One tier's cell of an option: its value and whether it is a proportional or an aggressive cell.</summary>
public readonly record struct MemoryCpuTierCellResolution(MemoryCpuTierValue Value, string Category);

/// <summary>One option after the settings owner resolved it: unit and range, the five tier cells,
/// the effective value, its category in the current tier, whether it came from the tier table or the
/// user's value, and when it takes effect.</summary>
public sealed record MemoryCpuTierOptionResolution(
    string Name,
    string? Unit,
    MemoryCpuTierRange Range,
    ImmutableArray<MemoryCpuTierCellResolution> Tiers,
    MemoryCpuTierValue Value,
    string Category,
    string Source,
    string Apply);

/// <summary>Why a tier or an override was refused: a stable code and the settings path it refers to.</summary>
public sealed record MemoryCpuTierRejection(string Code, string Path);

/// <summary>How the process-inventory workspace is returned: <c>ReleaseAfterUse</c> frees it after each
/// parse, otherwise it shrinks after <c>ShrinkAfterLowReads</c> consecutive low-use reads; zero there never
/// shrinks and keeps the previous peak (keepPeak).</summary>
public readonly record struct BufferReturnPolicy(bool ReleaseAfterUse, int ShrinkAfterLowReads)
{
    public bool KeepPeak => !ReleaseAfterUse && ShrinkAfterLowReads == 0;
}

/// <summary>Typed effective values for the wired option owners. Owners read these on their next use;
/// they never look up the table or compare tier numbers. Options not wired in this step
/// (ledgerMemoryModeFloor, nativeWorkareaGrowth) are in the resolution's options only.</summary>
public sealed record MemoryCpuTierEffectiveValues(
    long? GcGen0MaxBudget,
    int GcConserveMemory,
    BufferReturnPolicy BufferReturn,
    TimeSpan SingleRequestStopBuffer,
    TimeSpan ServiceIdentityCacheLifetime,
    TimeSpan BrowserRuntimeSnapshot,
    TimeSpan PlatformSensorSnapshot,
    TimeSpan StorageSnapshot,
    int RendererAbsenceEntries,
    int FileQuerySessionsMax,
    int FileQuerySessionsPrebuilt,
    bool FileQueryCloseIdleSessions);

/// <summary>The settings owner's published resolution (state item MemoryCpuTierEffective).
/// <c>Ignored</c> lists stored entries that no longer satisfy the option catalog (for example a settings
/// file edited by hand or written by another build); they are kept in the file, not applied, and reported.</summary>
public sealed record MemoryCpuTierResolution(
    int Tier,
    ImmutableArray<MemoryCpuTierOptionResolution> Options,
    MemoryCpuTierEffectiveValues Values,
    ImmutableArray<MemoryCpuTierRejection> Ignored)
{
    /// <summary>Same tier, same effective values and sources, same ignored entries. The catalog and the
    /// tier cells are fixed for a build, so they need no comparison.</summary>
    public bool SameAs(MemoryCpuTierResolution? other)
    {
        if (other is null || other.Tier != Tier || other.Values != Values
            || other.Options.Length != Options.Length || other.Ignored.Length != Ignored.Length) return false;
        for (var index = 0; index < Options.Length; index++)
        {
            var (left, right) = (Options[index], other.Options[index]);
            if (left.Name != right.Name || left.Value != right.Value || left.Source != right.Source) return false;
        }
        for (var index = 0; index < Ignored.Length; index++)
            if (Ignored[index] != other.Ignored[index]) return false;
        return true;
    }
}
