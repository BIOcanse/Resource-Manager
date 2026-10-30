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

    /// <summary>The settings write gate's check: tier in 1..5, every override names a known option and
    /// carries a value inside that option's range. Null settings are the defaults and pass.</summary>
    public static MemoryCpuTierRejection? Validate(MemoryCpuTierSettings? settings)
    {
        if (settings is null) return null;
        if (settings.Tier is < MemoryCpuTierSettings.MinimumTier or > MemoryCpuTierSettings.MaximumTier)
            return new(TierOutOfRange, TierPath);
        if (settings.Overrides is null) return null;
        foreach (var (name, value) in settings.Overrides.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            if (CheckOverride(name, value) is { } rejection) return rejection;
        return null;
    }

    /// <summary>A build-time table check, run when the table is compiled: five cells per option, every
    /// cell inside the option's range, and every category one of the two categories.</summary>
    public static void ValidateTable(CompiledMemoryCpuTierTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var option in Options)
        {
            var column = option.Column(table);
            if (column.IsDefault || column.Length != MemoryCpuTierSettings.MaximumTier)
                throw new InvalidDataException($"Memory/CPU tier option '{option.Name}' must have five tier cells.");
            foreach (var cell in column)
            {
                if (cell is null || cell.Category is not (MemoryCpuTierCategories.Proportional or MemoryCpuTierCategories.Aggressive))
                    throw new InvalidDataException($"Memory/CPU tier option '{option.Name}' has a cell without a valid category.");
                if (!TryParse(option, cell.Value, out _))
                    throw new InvalidDataException($"Memory/CPU tier option '{option.Name}' has a cell outside its range.");
            }
        }
    }

    /// <summary>Resolves a validated table and the stored settings. A stored tier outside 1..5 resolves as
    /// the default tier and a stored override outside the catalog is not applied; both are listed in
    /// <see cref="MemoryCpuTierResolution.Ignored"/>, and the settings file keeps them unchanged.</summary>
    public static MemoryCpuTierResolution Resolve(CompiledMemoryCpuTierTable table, MemoryCpuTierSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(table);
        var ignored = ImmutableArray.CreateBuilder<MemoryCpuTierRejection>();
        var tier = settings?.Tier ?? MemoryCpuTierSettings.DefaultTier;
        if (tier is < MemoryCpuTierSettings.MinimumTier or > MemoryCpuTierSettings.MaximumTier)
        {
            ignored.Add(new(TierOutOfRange, TierPath));
            tier = MemoryCpuTierSettings.DefaultTier;
        }
        var overrides = settings?.Overrides ?? new Dictionary<string, JsonElement>();
        foreach (var (name, value) in overrides.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            if (CheckOverride(name, value) is { } rejection) ignored.Add(rejection);

        var options = ImmutableArray.CreateBuilder<MemoryCpuTierOptionResolution>(Options.Length);
        foreach (var option in Options)
        {
            var column = option.Column(table);
            var cells = ImmutableArray.CreateBuilder<MemoryCpuTierCellResolution>(column.Length);
            foreach (var cell in column)
            {
                if (!TryParse(option, cell.Value, out var cellValue))
                    throw new InvalidDataException($"Memory/CPU tier option '{option.Name}' has a cell outside its range.");
                cells.Add(new(cellValue, cell.Category));
            }
            var tierCell = cells[tier - 1];
            var value = tierCell.Value;
            var source = MemoryCpuTierSources.Tier;
            if (overrides.TryGetValue(option.Name, out var overrideValue) && TryParse(option, overrideValue, out var user))
            {
                value = user;
                source = MemoryCpuTierSources.User;
            }
            options.Add(new(option.Name, option.Unit, option.Range, cells.MoveToImmutable(), value,
                tierCell.Category, source, option.Apply));
        }
        var resolved = options.MoveToImmutable();
        return new(tier, resolved, ToValues(resolved), ignored.ToImmutable());
    }

    /// <summary>Parses one JSON value against an option's range: a JSON integer for integer options, a JSON
    /// string for choice options, null only where the range allows "not set".</summary>
    public static bool TryParse(MemoryCpuTierOptionDefinition option, JsonElement? element, out MemoryCpuTierValue value)
    {
        ArgumentNullException.ThrowIfNull(option);
        value = MemoryCpuTierValue.Unset;
        if (element is not { } json || json.ValueKind == JsonValueKind.Null)
            return option.Range.Accepts(value);
        if (option.Range.Kind == MemoryCpuTierRangeKinds.Integer)
        {
            if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt64(out var integer)) return false;
            value = MemoryCpuTierValue.Of(integer);
        }
        else
        {
            if (json.ValueKind != JsonValueKind.String) return false;
            value = MemoryCpuTierValue.Of(json.GetString()!);
        }
        return option.Range.Accepts(value);
    }

    private static MemoryCpuTierRejection? CheckOverride(string name, JsonElement value)
    {
        var path = $"{OverridesPath}.{name}";
        if (Find(name) is not { } option) return new(UnknownOption, path);
        return TryParse(option, value, out _) ? null : new(ValueOutOfRange, path);
    }

    private static MemoryCpuTierEffectiveValues ToValues(ImmutableArray<MemoryCpuTierOptionResolution> options)
    {
        MemoryCpuTierValue Get(string name)
        {
            foreach (var option in options)
                if (option.Name == name) return option.Value;
            throw new InvalidOperationException($"Memory/CPU tier option '{name}' is not resolved.");
        }
        int Count(string name) => checked((int)Get(name).Integer!.Value);
        TimeSpan Seconds(string name) => TimeSpan.FromSeconds(Get(name).Integer!.Value);
        TimeSpan Milliseconds(string name) => TimeSpan.FromMilliseconds(Get(name).Integer!.Value);
        var bufferReturn = Get(MemoryCpuTierOptionNames.BufferReturn).Choice switch
        {
            BufferReturnChoices.ReleaseAfterUse => new BufferReturnPolicy(true, 0),
            BufferReturnChoices.ShrinkAfter2LowReads => new BufferReturnPolicy(false, 2),
            BufferReturnChoices.ShrinkAfter8LowReads => new BufferReturnPolicy(false, 8),
            BufferReturnChoices.ShrinkAfter32LowReads => new BufferReturnPolicy(false, 32),
            BufferReturnChoices.KeepPeak => new BufferReturnPolicy(false, 0),
            var other => throw new InvalidOperationException($"Buffer return choice '{other}' is not mapped."),
        };
        return new(
            Get(MemoryCpuTierOptionNames.GcGen0MaxBudget).Integer,
            Count(MemoryCpuTierOptionNames.GcConserveMemory),
            bufferReturn,
            Milliseconds(MemoryCpuTierOptionNames.SingleRequestStopBufferMs),
            Milliseconds(MemoryCpuTierOptionNames.ServiceIdentityCacheMs),
            Seconds(MemoryCpuTierOptionNames.BrowserRuntimeSnapshotSeconds),
            Seconds(MemoryCpuTierOptionNames.PlatformSensorSnapshotSeconds),
            Seconds(MemoryCpuTierOptionNames.StorageSnapshotSeconds),
            Count(MemoryCpuTierOptionNames.RendererAbsenceEntries),
            Count(MemoryCpuTierOptionNames.FileQuerySessionsMax),
            Count(MemoryCpuTierOptionNames.FileQuerySessionsPrebuilt),
            Get(MemoryCpuTierOptionNames.FileQueryIdleSessions).Choice == FileQueryIdleSessionChoices.Close);
    }
}
