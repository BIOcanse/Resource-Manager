using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using FreedomPoint;
using FreedomPoint.Generators;

namespace ResourceManager.Ledger.Configuration;

/// <summary>Complete global and per-table ledger configuration catalog.</summary>
public sealed class LedgerPoints
{
    private const string DefaultSource = "ledger.declared_defaults";
    private const string ProjectionScope = "ledger.values";
    private const int PerTablePointCount = 13;
    private readonly ImmutableDictionary<PointPath, PointValueSet> defaults;
    private readonly ImmutableDictionary<string, PointPath> paths;

    public PointRegistry Registry { get; }
    public int TableCount { get; }

    public LedgerPoints(int tableCount = 1, ushort package = 1, ushort owner1 = 7, ushort owner2 = 0)
    {
        if (package == 0 || owner1 == 0 || owner1 > PointPath.MaximumOwner || owner2 > PointPath.MaximumOwner)
            throw new FreedomPointException("ledger_point_scope_invalid");
        if (tableCount < 1 || tableCount > PointPath.MaximumOwner - owner2)
            throw new FreedomPointException("ledger_table_count_invalid", detail: tableCount.ToString());

        TableCount = tableCount;
        var points = new List<PointDeclaration>(8 + PerTablePointCount * tableCount);
        var values = ImmutableDictionary.CreateBuilder<PointPath, PointValueSet>();
        var pointPaths = ImmutableDictionary.CreateBuilder<string, PointPath>(StringComparer.Ordinal);

        var bitmapWidth = Path(owner2, 0);
        AddGlobalUnsigned(1, "uid_width_bits", 128, 128, 256, "bits",
            "Global build-time UID width; the unsigned step restricts values to 128 or 256.", step: 128);
        AddGlobalUnsigned(0, "activity_bitmap_width", 8, 8, 64, "bits",
            "Global build-time bitmap width. Supported values are exactly 8, 16, 32 and 64.");
        AddGlobalUnsigned(2, "development_allocation_statistics", 0, 0, 1, "enabled",
            "Build-time development allocation facts; zero removes Native collection and query code.");
        AddGlobalUnsigned(3, "development_lease_analysis", 0, 0, 1, "enabled",
            "Build-time lease usage analysis; zero removes Native analysis and write observation.");
        AddGlobalUnsigned(4, "development_lease_analysis_capacity", 256, 1, 65536, "records",
            "Global build-time bounded lease analysis record capacity.");

        AddGlobalUnsigned(5, "use_window_cache", 0, 0, 1, "enabled",
            "Build-time internal window borrow optimization. Call syntax and real pin safety are unchanged.");
        AddGlobalUnsigned(6, "use_window_cache_slots", 64, 1, 65536, "hints",
            "Bounded internal slot hint count, not a second UID index.");
        AddGlobalUnsigned(7, "development_lease_analysis_write_watch_pages", 16384, 1, 1048576, "pages",
            "Build-time write observation scratch bound; oversized regions are reported unavailable.");
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            var tableOwner2 = checked((ushort)(owner2 + tableIndex + 1));
            AddTableUnsigned(tableIndex, tableOwner2, 0, "activity_score_epochs", 8, 1, null, "epochs",
                "Per-table K: number of recent epochs used for activity scoring.", maximumPoint: bitmapWidth);
            AddTableUnsigned(tableIndex, tableOwner2, 1, "idle_epochs", 8, 0, null, "epochs",
                "Per-table N: inactive epochs required before idle unload; zero disables idle unload.",
                maximumPoint: bitmapWidth);

            AddTableUnsigned(tableIndex, tableOwner2, 2, "concentrated_trigger_percent", 10, 0, 100, "percent",
                "Per-table concentrated trigger; zero disables concentrated capacity protection.");
            AddTableUnsigned(tableIndex, tableOwner2, 3, "concentrated_target_percent", 20, 0, 100, "percent",
                "Per-table concentrated capacity-protection target.");
            AddTableUnsigned(tableIndex, tableOwner2, 4, "smooth_trigger_percent", 15, 0, 100, "percent",
                "Per-table smooth capacity-protection trigger.");
            AddTableUnsigned(tableIndex, tableOwner2, 5, "smooth_emergency_percent", 5, 0, 100, "percent",
                "Per-table inclusive smooth emergency threshold.");
            AddTableUnsigned(tableIndex, tableOwner2, 6, "smooth_interval_epochs", 1, 1, null, "epochs",
                "Per-table smooth release interval in epochs.");
            AddTableUnsigned(tableIndex, tableOwner2, 7, "smooth_release_count", 1, 1, null, "actions",
                "Per-table number of smooth releases per interval.");
            AddTableUnsigned(tableIndex, tableOwner2, 8, "tick_maximum_actions", 64, 1, null, "actions",
                "Per-table upper bound shared by the three Tick stages.");
            AddTableChoice(tableIndex, tableOwner2, 9, "release_check_enabled", ["false", "true"], 0, "boolean",
                "Per-table switch for the pre-release allocation check.");
            AddTableUnsigned(tableIndex, tableOwner2, 10, "release_tolerance_parts_per_million", 0, 0,
                uint.MaxValue, "ppm",
                "Per-table release-check relative tolerance; 10000 parts per million is one percent.");
            AddTableUnsigned(tableIndex, tableOwner2, 11, "release_tolerance_bytes", 0, 0,
                ulong.MaxValue, "bytes",
                "Per-table release-check absolute tolerance in bytes.");
            AddTableChoice(tableIndex, tableOwner2, 12, "capacity_protection_method",
                ["concentrated", "smooth"], 0, "method", "Per-table capacity protection method.");
        }

        Registry = new(points);
        defaults = values.ToImmutable();
        paths = pointPaths.ToImmutable();

        PointPath Path(ushort pointOwner2, int index) => new(package, owner1, pointOwner2,
            PointFamily.Build, PointKind.Simple, checked((ushort)index));

        void AddGlobalUnsigned(int index, string name, ulong value, ulong minimum, ulong? maximum,
            string unit, string reason, ulong? step = null)
        {
            var path = Path(owner2, index);
            AddUnsigned(path, "ledger." + name, name, value, minimum, maximum, unit, reason, step: step);
        }

        void AddTableUnsigned(int tableIndex, ushort tableOwner2, int index, string name, ulong value,
            ulong minimum, ulong? maximum, string unit, string reason, PointPath? maximumPoint = null)
        {
            var qualifiedName = $"ledger.table.{tableIndex}.{name}";
            AddUnsigned(Path(tableOwner2, index), qualifiedName, $"table_{tableIndex}_{name}", value,
                minimum, maximum, unit, reason, maximumPoint);
        }

        void AddTableChoice(int tableIndex, ushort tableOwner2, int index, string name,
            ImmutableArray<string> options, int defaultOption, string unit, string reason)
        {
            var qualifiedName = $"ledger.table.{tableIndex}.{name}";
            AddChoice(Path(tableOwner2, index), qualifiedName, $"table_{tableIndex}_{name}",
                options, defaultOption, unit, reason);
        }

        void AddUnsigned(PointPath path, string qualifiedName, string symbol, ulong value,
            ulong minimum, ulong? maximum, string unit, string reason,
            PointPath? maximumPoint = null, ulong? step = null)
        {
            var slot = new SlotContract
            {
                Kind = ValueKind.UnsignedInteger,
                Unit = unit,
                UnsignedMinimum = minimum,
                UnsignedMaximum = maximum,
                UnsignedMaximumPoint = maximumPoint,
                UnsignedStep = step
            };
            Add(path, qualifiedName, symbol, slot, PointValue.FromUnsignedInteger(value), reason);
        }

        void AddChoice(PointPath path, string qualifiedName, string symbol,
            ImmutableArray<string> options, int defaultOption, string unit, string reason)
        {
            var slot = new SlotContract
            {
                Kind = ValueKind.Choose,
                Unit = unit,
                Options = options,
                RequiredSelectionCount = 1
            };
            Add(path, qualifiedName, symbol, slot, PointValue.FromChoices(1UL << defaultOption), reason);
        }

        void Add(PointPath path, string qualifiedName, string symbol, SlotContract slot,
            PointValue value, string reason)
        {
            points.Add(new PointDeclaration
            {
                Path = path,
                Name = qualifiedName,
                Status = DeclarationStatus.Active,
                Description = reason,
                Update = UpdateClass.RebuildArtifact,
                ValueSource = DefaultSource,
                SourcePointer = qualifiedName,
                DefaultReason = reason,
                Slots = [slot],
                GenerationTargets =
                [
                    new() { Language = GenerationLanguage.CSharp, Form = GenerationForm.BuildConstant,
                        Scope = ProjectionScope, Symbol = symbol },
                    new() { Language = GenerationLanguage.Zig, Form = GenerationForm.BuildConstant,
                        Scope = ProjectionScope, Symbol = symbol }
                ]
            });
            values.Add(path, PointValueSet.Simple(value));
            pointPaths.Add(qualifiedName, path);
        }
    }

    /// <summary>
    /// Merge declared defaults and typed overrides through FreedomPoint. This
    /// does not validate numeric point contracts; build inputs must also pass
    /// <see cref="Check"/> before generation is used for a build.
    /// </summary>
    public ResolvedFacts Resolve(IEnumerable<FactOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var facts = FactResolver.Resolve(Registry,
            new Dictionary<string, Func<PointDeclaration, PointValueSet>>(StringComparer.Ordinal)
            {
                [DefaultSource] = point => defaults[point.Path]
            }, overrides);
        return facts;
    }

    /// <summary>Run full developer-time point and ledger build-contract checks.</summary>
    public ResolvedFacts Check(IEnumerable<FactOverride> overrides)
    {
        var facts = Resolve(overrides);
        FactResolver.ValidateContracts(Registry, facts,
            new Dictionary<string, Action<JsonElement>>(StringComparer.Ordinal));
        ValidateBuildContracts(facts);
        return facts;
    }

    public FactOverride Override(string name, JsonElement value)
    {
        if (!paths.TryGetValue(name, out var path))
            throw new FreedomPointException("unknown_ledger_point", detail: name);
        var point = Registry.Require(path);
        var slot = point.Slots[0];
        var selected = slot.Kind switch
        {
            ValueKind.UnsignedInteger when value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)
                => PointValue.FromUnsignedInteger(number),
            ValueKind.Choose => ParseChoice(point, value),
            _ => throw new FreedomPointException("ledger_override_value_invalid", path)
        };
        return new(path, PointValueSet.Simple(selected), "adopter.build_input");
    }

    public Projection Emit(ResolvedFacts facts, string sourceSeal, string toolchainSeal)
        => TypedProjection.Emit(Registry, facts,
            new ProjectionTarget(ProjectionScope, "ResourceManager.Ledger.Configuration.Generated", "LedgerDefaults")
            {
                ZigStructureModule = "ledger_structure"
            },
            sourceSeal,
            toolchainSeal,
            new Dictionary<string, ComplexDeclarationEmitter>(StringComparer.Ordinal),
            new Dictionary<string, ComplexEmitter>(StringComparer.Ordinal));

    private void ValidateBuildContracts(ResolvedFacts facts)
    {
        ulong Unsigned(string name) => facts.Require(paths[name]).Value.Slots[0].UnsignedInteger;

        if (Unsigned("ledger.activity_bitmap_width") is not (8 or 16 or 32 or 64))
            throw new FreedomPointException("ledger_bitmap_width_not_supported", paths["ledger.activity_bitmap_width"]);

        for (var tableIndex = 0; tableIndex < TableCount; tableIndex++)
        {
            var prefix = $"ledger.table.{tableIndex}.";
            if (Unsigned(prefix + "concentrated_trigger_percent") >=
                Unsigned(prefix + "concentrated_target_percent"))
                throw new FreedomPointException("ledger_concentrated_threshold_order",
                    paths[prefix + "concentrated_trigger_percent"]);
            if (Unsigned(prefix + "smooth_emergency_percent") >= Unsigned(prefix + "smooth_trigger_percent"))
                throw new FreedomPointException("ledger_smooth_threshold_order",
                    paths[prefix + "smooth_emergency_percent"]);
        }
    }

    private static PointValue ParseChoice(PointDeclaration point, JsonElement value)
    {
        var selected = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
        var index = selected is null ? -1 : point.Slots[0].Options.IndexOf(selected);
        if (index < 0)
            throw new FreedomPointException("ledger_choice_invalid", point.Path);
        return PointValue.FromChoices(1UL << index);
    }
}
