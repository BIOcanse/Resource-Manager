using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomPoint;
using FreedomPoint.Generators;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.Shared.Appearance;

namespace ResourceManager.FrontendPreferencesBuild;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length is not (2 or 4)) throw new ArgumentException("Usage: <repository-root> <evidence-output> [native-compiler-context typescript-context]");
            Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args.Length == 4 ? args[2] : null, args.Length == 4 ? args[3] : null);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string root, string output, string? compilerContext, string? typeScriptContext)
    {
        Directory.CreateDirectory(output);
        var backendPath = Path.Combine(root, "src/Core/Configuration/FreedomPoints/backend.json");
        var host = FreedomPointRegistry.Parse(File.ReadAllBytes(backendPath));
        var basis = host.Sdk.Registry;
        // Declaration identity is additive metadata. The original constructor, source
        // traversal and target map produce exactly the same ordered point bytes.
        var oldView = new PointRegistry(basis.Points);
        var before = JsonSerializer.SerializeToUtf8Bytes(oldView.Points);
        var after = JsonSerializer.SerializeToUtf8Bytes(basis.Points);
        if (!before.AsSpan().SequenceEqual(after)) throw new InvalidDataException("Backend point bytes changed.");
        File.WriteAllBytes(Path.Combine(output, "backend-points.before.json"), before);
        File.WriteAllBytes(Path.Combine(output, "backend-points.after.json"), after);
        var partitionPath = Path.Combine(root, "src/Core/Configuration/FreedomPoints/backend.groups.json");
        var partition = JsonNode.Parse(File.ReadAllBytes(partitionPath))!.AsObject();
        if (partition["contractFingerprint"]!.GetValue<string>() != basis.ContractFingerprint)
            throw new InvalidDataException("Backend grouping fingerprint must be updated by the backend declaration owner.");

        using var profile = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/HostManager/default.json")));
        using var cpuDefaults = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/Cpu/baseline-defaults.json")));
        var cpu = JsonSerializer.SerializeToElement(new FreedomPointRegistry.CpuScoringInput(
            cpuDefaults.RootElement.GetProperty("unmatchedRatio").GetDouble(), CompiledCpuCoreWeights.Empty),
            FreedomPointRegistry.CreateSerializerOptions());
        var inherited = host.Sdk.Resolve(profile.RootElement, cpu);
        var declarationPath = Path.Combine(root, "src/UI/Frontend/preferences.points.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(declarationPath));
        var definitions = document.RootElement.GetProperty("points").EnumerateArray().Select(value => value.Clone()).ToArray();
        if (definitions.Length != 30) throw new InvalidDataException("Expected 30 frontend points.");
        var style = definitions.Single(item => item.GetProperty("symbol").GetString() == "UiStyle").GetProperty("value").GetString()!;
        var theme = definitions.Single(item => item.GetProperty("symbol").GetString() == "Theme").GetProperty("value").GetString()!;
        var appearance = UiStyleCatalog.Normalize(style, theme);
        if (appearance.UiStyle != style || appearance.Theme != theme)
            throw new InvalidDataException("Frontend appearance points must select a palette supported by their style.");
        var layout = basis.CreateExtensionLayout("resource-manager-backend", "frontend", 5);
        var points = ImmutableArray.CreateBuilder<PointDeclaration>();
        var counts = new Dictionary<(ushort Group, PointKind Kind), ushort>();
        var values = new Dictionary<PointPath, PointValueSet>();
        var validators = new Dictionary<string, Action<JsonElement>>();
        var declarations = new Dictionary<string, ComplexDeclarationEmitter>();
        var emitters = new Dictionary<string, ComplexEmitter>();
        var tsDeclarations = new Dictionary<string, TypeScriptComplexDeclarationEmitter>();
        var tsEmitters = new Dictionary<string, TypeScriptComplexEmitter>();
        foreach (var definition in definitions)
        {
            var type = definition.GetProperty("type").GetString()!;
            var scalar = type is "integer" or "decimal";
            var group = definition.GetProperty("group").GetUInt16();
            var kind = scalar ? PointKind.Simple : PointKind.Complex;
            var key = (group, kind);
            var index = counts.GetValueOrDefault(key);
            counts[key] = checked((ushort)(index + 1));
            var path = layout.Path(group, PointFamily.Build, kind, index);
            var name = layout.Prefix + "-" + definition.GetProperty("name").GetString();
            var symbol = definition.GetProperty("symbol").GetString()!;
            var contract = scalar ? null : "frontend." + symbol;
            var point = new PointDeclaration
            {
                Path = path, Name = name, Status = DeclarationStatus.Active, Update = UpdateClass.RebuildArtifact,
                ValueSource = "frontend.defaults", SourcePointer = "/points/" + Array.IndexOf(definitions, definition) + "/value",
                ComplexContract = contract,
                Slots = scalar ? [type == "integer"
                    ? new SlotContract { Kind = ValueKind.Integer, IntegerBitWidth = 32,
                        IntegerMinimum = definition.GetProperty("min").GetInt32(), IntegerMaximum = definition.GetProperty("max").GetInt32() }
                    : new SlotContract { Kind = ValueKind.Decimal, DecimalMinimum = definition.GetProperty("min").GetDouble(),
                        DecimalMaximum = definition.GetProperty("max").GetDouble() }] : [],
                GenerationTargets = [new() { Language = GenerationLanguage.TypeScript, Form = GenerationForm.BuildConstant, Scope = "frontend", Symbol = symbol },
                    new() { Language = GenerationLanguage.CSharp, Form = GenerationForm.BuildConstant, Scope = "frontend", Symbol = symbol }]
            };
            points.Add(point);
            var value = definition.GetProperty("value");
            values.Add(path, scalar ? PointValueSet.Simple(type == "integer" ? PointValue.FromInteger(value.GetInt32())
                : PointValue.FromDecimal(value.GetDouble())) : PointValueSet.ComplexValue(value));
            if (contract is null) continue;
            validators.Add(contract, value => Validate(type, definition, value));
            declarations.Add(contract, _ => CSharpDeclaration(type));
            emitters.Add(contract, (_, fact) => new(CSharpValue(type, fact.Value.Complex!.Value), "undefined"));
            tsDeclarations.Add(contract, _ => new(TypeScriptType(type, definition), string.Empty));
            tsEmitters.Add(contract, (_, fact) => new(fact.Value.Complex!.Value.GetRawText()));
        }
        var extension = new RegistryExtension(layout, points.ToImmutable(), ["frontend", "native-shell"], []);
        var view = basis.Extend(extension);
        var facts = FactResolver.ResolveExtension(view, inherited,
            new Dictionary<string, Func<PointDeclaration, PointValueSet>> { ["frontend.defaults"] = point => values[point.Path] }, []);
        // Validate own contracts without inventing another interpreter for backend complex types.
        var own = new PointRegistry(extension.Declarations);
        var ownFacts = FactResolver.Resolve(own,
            new Dictionary<string, Func<PointDeclaration, PointValueSet>> { ["frontend.defaults"] = point => values[point.Path] }, []);
        FactResolver.ValidateContracts(own, ownFacts, validators);
        var sourceSeal = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(declarationPath)));
        var toolSeal = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TypedProjection).Assembly.Location)));
        var cs = TypedProjection.Emit(view, facts, new("frontend", "ResourceManager.NativeUi.Preferences", "FrontendPoints"),
            sourceSeal, toolSeal, declarations, emitters);
        var ts = TypedProjection.EmitTypeScript(view, facts,
            new("frontend", "ResourceManager", "FrontendPoints") { TypeScriptStructureModuleSpecifier = "./frontendPoints.structure.ts" },
            sourceSeal, toolSeal, tsDeclarations, tsEmitters);
        Write(Path.Combine(root, "src/UI/Core/Preferences/Generated/FrontendPoints.Structure.g.cs"), cs.CSharp.StructureSource);
        Write(Path.Combine(root, "src/UI/Core/Preferences/Generated/FrontendPoints.Values.g.cs"), cs.CSharp.ValuesSource);
        Write(Path.Combine(root, "src/UI/Frontend/src/preferences/generated/frontendPoints.structure.ts"), ts.Sources.StructureSource);
        Write(Path.Combine(root, "src/UI/Frontend/src/preferences/generated/frontendPoints.values.ts"), ts.Sources.ValuesSource);
        File.WriteAllText(Path.Combine(output, "extension.json"), JsonSerializer.Serialize(extension, Json));
        File.WriteAllText(Path.Combine(output, "bindings.json"), JsonSerializer.Serialize(new { cs.SymbolBindings, ts.MemberBindings, ts.ContractBindings }, Json));
        File.WriteAllText(Path.Combine(output, "generation.json"), JsonSerializer.Serialize(new
        {
            passed = true, points = points.Count, basePoints = basis.Points.Length, layout,
            basePointBytesIdentical = true, basePointSha256 = Convert.ToHexString(SHA256.HashData(before)),
            basis.ContractFingerprint, sourceSeal, toolSeal
        }, Json));
        Console.WriteLine($"Generated {points.Count} frontend points; preserved {basis.Points.Length} backend points byte-for-byte.");
        if (compilerContext is not null && typeScriptContext is not null)
            PreferenceSourceCheck.Run(view, ts, root, output, compilerContext, typeScriptContext);
    }

    // Generated sources are committed. A checkout may hold them with CRLF (core.autocrlf), so an equal text
    // with other line endings is left untouched; rewriting it would make a clean release tree dirty.
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
            File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void Validate(string type, JsonElement definition, JsonElement value)
    {
        var valid = type switch
        {
            "choice" => value.ValueKind == JsonValueKind.String && definition.GetProperty("options").EnumerateArray().Any(option => option.GetString() == value.GetString()),
            "refresh" => value.ValueKind == JsonValueKind.Object && value.GetProperty("mode").GetString() is "aotu" or "preset" or "custom"
                && value.GetProperty("preset").GetString() is "responsive" or "balanced" or "lowPower" or "quiet"
                && value.GetProperty("customValue").GetInt32() is >= 500 and <= 300000,
            "presets" => new[] { "responsive", "balanced", "lowPower", "quiet" }.All(name => value.GetProperty(name).GetInt32() is >= 500 and <= 300000),
            "metricOrder" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 64
                && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 128 })
                && value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.GetArrayLength(),
            "dashboardDefaults" => ValidateDashboardDefaults(value),
            "dashboardLimits" => value.GetProperty("smallMetricLimit").GetInt32() is >= 0 and <= 64
                && value.GetProperty("minimumColumnWidth").GetInt32() > 0
                && value.GetProperty("maximumColumnWidth").GetInt32() >= value.GetProperty("minimumColumnWidth").GetInt32()
                && value.GetProperty("defaultColumnWidth").GetInt32() >= value.GetProperty("minimumColumnWidth").GetInt32()
                && value.GetProperty("defaultColumnWidth").GetInt32() <= value.GetProperty("maximumColumnWidth").GetInt32(),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid frontend point contract: " + definition.GetProperty("symbol").GetString());
    }

    private static ComplexDeclarationProjection CSharpDeclaration(string type) => type switch
    {
        "choice" => new("string", "[]const u8", "", ""),
        "refresh" => new("PointRefreshSetting", "void", "public sealed record PointRefreshSetting(string Mode, string Preset, int CustomValue);", ""),
        "presets" => new("PointRefreshPresets", "void", "public sealed record PointRefreshPresets(int Responsive, int Balanced, int LowPower, int Quiet);", ""),
        "metricOrder" => new("ImmutableArray<string>", "void", "", ""),
        "dashboardDefaults" => new("global::System.Text.Json.JsonElement", "void", "", ""),
        "dashboardLimits" => new("PointDashboardLayoutLimits", "void", "public sealed record PointDashboardLayoutLimits(int SmallMetricLimit, int MinimumColumnWidth, int MaximumColumnWidth, int DefaultColumnWidth);", ""),
        _ => throw new InvalidDataException(type)
    };
    private static string CSharpValue(string type, JsonElement value) => type switch
    {
        "choice" => JsonSerializer.Serialize(value.GetString()),
        "refresh" => "new(" + string.Join(",", JsonSerializer.Serialize(value.GetProperty("mode").GetString()),
            JsonSerializer.Serialize(value.GetProperty("preset").GetString()), value.GetProperty("customValue").GetRawText()) + ")",
        "presets" => "new(" + string.Join(",", new[] { "responsive", "balanced", "lowPower", "quiet" }.Select(name => value.GetProperty(name).GetRawText())) + ")",
        "metricOrder" => "ImmutableArray.Create<string>(" + string.Join(",", value.EnumerateArray().Select(item => JsonSerializer.Serialize(item.GetString()))) + ")",
        "dashboardDefaults" => "global::System.Text.Json.JsonSerializer.Deserialize<global::System.Text.Json.JsonElement>(" + JsonSerializer.Serialize(value.GetRawText()) + ")",
        "dashboardLimits" => "new(" + string.Join(",", new[] { "smallMetricLimit", "minimumColumnWidth", "maximumColumnWidth", "defaultColumnWidth" }.Select(name => value.GetProperty(name).GetRawText())) + ")",
        _ => throw new InvalidDataException(type)
    };
    private static string TypeScriptType(string type, JsonElement definition) => type switch
    {
        "choice" => string.Join(" | ", definition.GetProperty("options").EnumerateArray().Select(value => JsonSerializer.Serialize(value.GetString()))),
        "refresh" => "{ readonly mode: \"aotu\" | \"preset\" | \"custom\"; readonly preset: \"responsive\" | \"balanced\" | \"lowPower\" | \"quiet\"; readonly customValue: number }",
        "presets" => "{ readonly responsive: number; readonly balanced: number; readonly lowPower: number; readonly quiet: number }",
        "metricOrder" => "readonly string[]",
        "dashboardDefaults" => "{ readonly builtInMetricIds: readonly string[]; readonly cards: readonly { readonly id: string; readonly main: string; readonly small: readonly string[]; readonly scope: \"system\" | \"gpu\"; readonly required: boolean }[]; readonly resourceBars: readonly { readonly id: string; readonly metricId: string; readonly scaleMode: \"capacity\" | \"active\"; readonly scope: \"system\" | \"gpu\"; readonly required: boolean }[]; readonly softwareColumns: readonly { readonly id: string; readonly visible: boolean; readonly width: number; readonly scope: \"system\" | \"gpu\" }[]; readonly processColumns: readonly { readonly id: string; readonly visible: boolean; readonly width: number; readonly scope: \"system\" | \"gpu\" }[] }",
        "dashboardLimits" => "{ readonly smallMetricLimit: number; readonly minimumColumnWidth: number; readonly maximumColumnWidth: number; readonly defaultColumnWidth: number }",
        _ => throw new InvalidDataException(type)
    };

    private static bool ValidateDashboardDefaults(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "builtInMetricIds", "cards", "resourceBars", "softwareColumns", "processColumns" })
            if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 10000) return false;
        if (value.GetProperty("builtInMetricIds").EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))) return false;
        foreach (var name in new[] { "cards", "resourceBars", "softwareColumns", "processColumns" })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.GetProperty(name).EnumerateArray())
            {
                if (item.GetProperty("id").GetString() is not { Length: > 0 } id || !ids.Add(id)
                    || item.GetProperty("scope").GetString() is not ("system" or "gpu")) return false;
                if (name == "cards" && (item.GetProperty("main").GetString() is not { Length: > 0 }
                    || item.GetProperty("small").ValueKind != JsonValueKind.Array
                    || item.GetProperty("small").EnumerateArray().Any(metric => metric.GetString() is not { Length: > 0 }))) return false;
                if (name == "resourceBars" && (item.GetProperty("metricId").GetString() is not { Length: > 0 }
                    || item.GetProperty("scaleMode").GetString() is not ("active" or "capacity"))) return false;
                if (name is "softwareColumns" or "processColumns" && (!item.GetProperty("width").TryGetDouble(out var width)
                    || !double.IsFinite(width) || width <= 0)) return false;
                _ = name is "cards" or "resourceBars" ? item.GetProperty("required").GetBoolean() : item.GetProperty("visible").GetBoolean();
            }
        }
        return true;
    }
}
