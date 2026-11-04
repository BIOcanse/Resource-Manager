using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

/// <summary>One option of the memory/CPU tier catalog: its wire name, unit, accepted range, when a change
/// takes effect, and the tier-table column that holds its five cells.</summary>
public sealed record MemoryCpuTierOptionDefinition(
    string Name,
    string? Unit,
    MemoryCpuTierRange Range,
    string Apply,
    Func<CompiledMemoryCpuTierTable, ImmutableArray<CompiledMemoryCpuTierCell>> Column);

/// <summary>
/// The only reader of the memory/CPU tier table. Tier table plus the user's overrides give, per option,
/// the effective value, the current tier's category, the source (tier or user) and the apply mode.
/// Units, ranges and apply modes are this catalog; the five values and categories are the freedom points.
/// </summary>
public static class MemoryCpuTierResolver
{
    public const string TierOutOfRange = "memory-cpu-tier-out-of-range";
    public const string UnknownOption = "memory-cpu-tier-unknown-option";
    public const string ValueOutOfRange = "memory-cpu-tier-value-out-of-range";
    public const string TierPath = "$.memoryCpuTier.tier";
    public const string OverridesPath = "$.memoryCpuTier.overrides";
    private const long MaximumCacheSeconds = 3_600;
    private const long MaximumCacheMilliseconds = MaximumCacheSeconds * 1_000;

    public static ImmutableArray<MemoryCpuTierOptionDefinition> Options { get; } =
    [
        new(MemoryCpuTierOptionNames.GcGen0MaxBudget, MemoryCpuTierUnits.Bytes,
            MemoryCpuTierRange.Integer(64 * 1024, 1024L * 1024 * 1024, unset: true),
            MemoryCpuTierApplyModes.Restart, static table => table.GcGen0MaxBudget),
        new(MemoryCpuTierOptionNames.GcConserveMemory, MemoryCpuTierUnits.Level, MemoryCpuTierRange.Integer(0, 9),
            MemoryCpuTierApplyModes.Restart, static table => table.GcConserveMemory),
        new(MemoryCpuTierOptionNames.LedgerMemoryModeFloor, null,
            MemoryCpuTierRange.Choice(LedgerMemoryModeFloorChoices.Optimize, LedgerMemoryModeFloorChoices.AutoThrifty,
                LedgerMemoryModeFloorChoices.Auto, LedgerMemoryModeFloorChoices.AutoGenerous,
                LedgerMemoryModeFloorChoices.Unrestricted),
            MemoryCpuTierApplyModes.Immediate, static table => table.LedgerMemoryModeFloor),
        new(MemoryCpuTierOptionNames.BufferReturn, null,
            MemoryCpuTierRange.Choice(BufferReturnChoices.ReleaseAfterUse, BufferReturnChoices.ShrinkAfter2LowReads,
                BufferReturnChoices.ShrinkAfter8LowReads, BufferReturnChoices.ShrinkAfter32LowReads,
                BufferReturnChoices.KeepPeak),
            MemoryCpuTierApplyModes.Immediate, static table => table.BufferReturn),
        new(MemoryCpuTierOptionNames.NativeWorkareaGrowth, null,
            MemoryCpuTierRange.Choice(NativeWorkareaGrowthChoices.ExactRows, NativeWorkareaGrowthChoices.Rows125,
                NativeWorkareaGrowthChoices.Rows150, NativeWorkareaGrowthChoices.Rows200,
                NativeWorkareaGrowthChoices.ReserveCapacity),
            MemoryCpuTierApplyModes.Immediate, static table => table.NativeWorkareaGrowth),
        new(MemoryCpuTierOptionNames.SingleRequestStopBufferMs, MemoryCpuTierUnits.Milliseconds,
            MemoryCpuTierRange.Integer(0, MaximumCacheMilliseconds),
            MemoryCpuTierApplyModes.Immediate, static table => table.SingleRequestStopBufferMs),
        new(MemoryCpuTierOptionNames.ServiceIdentityCacheMs, MemoryCpuTierUnits.Milliseconds,
            MemoryCpuTierRange.Integer(0, MaximumCacheMilliseconds),
            MemoryCpuTierApplyModes.Immediate, static table => table.ServiceIdentityCacheMs),
        new(MemoryCpuTierOptionNames.BrowserRuntimeSnapshotSeconds, MemoryCpuTierUnits.Seconds,
            MemoryCpuTierRange.Integer(0, MaximumCacheSeconds),
            MemoryCpuTierApplyModes.Immediate, static table => table.BrowserRuntimeSnapshotSeconds),
        new(MemoryCpuTierOptionNames.PlatformSensorSnapshotSeconds, MemoryCpuTierUnits.Seconds,
            MemoryCpuTierRange.Integer(0, MaximumCacheSeconds),
            MemoryCpuTierApplyModes.Immediate, static table => table.PlatformSensorSnapshotSeconds),
        new(MemoryCpuTierOptionNames.StorageSnapshotSeconds, MemoryCpuTierUnits.Seconds,
            MemoryCpuTierRange.Integer(0, MaximumCacheSeconds),
            MemoryCpuTierApplyModes.Immediate, static table => table.StorageSnapshotSeconds),
        new(MemoryCpuTierOptionNames.RendererAbsenceEntries, MemoryCpuTierUnits.Count,
            MemoryCpuTierRange.Integer(1, 65_536),
            MemoryCpuTierApplyModes.Immediate, static table => table.RendererAbsenceEntries),
        new(MemoryCpuTierOptionNames.FileQuerySessionsMax, MemoryCpuTierUnits.Count,
            MemoryCpuTierRange.Integer(1, 8),
            MemoryCpuTierApplyModes.Immediate, static table => table.FileQuerySessionsMax),
        new(MemoryCpuTierOptionNames.FileQuerySessionsPrebuilt, MemoryCpuTierUnits.Count,
            MemoryCpuTierRange.Integer(0, 8),
            MemoryCpuTierApplyModes.Immediate, static table => table.FileQuerySessionsPrebuilt),
        new(MemoryCpuTierOptionNames.FileQueryIdleSessions, null,
            MemoryCpuTierRange.Choice(FileQueryIdleSessionChoices.Close, FileQueryIdleSessionChoices.Keep),
            MemoryCpuTierApplyModes.Immediate, static table => table.FileQueryIdleSessions),
    ];

    public static MemoryCpuTierOptionDefinition? Find(string name)
    {
        foreach (var option in Options)
            if (string.Equals(option.Name, name, StringComparison.Ordinal)) return option;
        return null;
    }

    public static MemoryCpuTierRejection? Validate(MemoryCpuTierSettings? settings)
        => NativeMemoryCpuTierParameters.Validate(settings);
    public static void ValidateTable(CompiledMemoryCpuTierTable table)
        => NativeMemoryCpuTierParameters.ValidateTable(table);
    public static MemoryCpuTierResolution Resolve(CompiledMemoryCpuTierTable table, MemoryCpuTierSettings? settings)
        => NativeMemoryCpuTierParameters.Resolve(table, settings);
    public static bool TryParse(MemoryCpuTierOptionDefinition option, JsonElement? element, out MemoryCpuTierValue value)
        => NativeMemoryCpuTierParameters.TryParse(option, element, out value);
}
