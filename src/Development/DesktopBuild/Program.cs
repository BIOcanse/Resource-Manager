using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreedomPoint;
using FreedomPoint.Generators;

namespace ResourceManager.DesktopBuild;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length is not (3 or 4) || args.Length == 4 && args[3] != "consumers")
                throw new ArgumentException("Usage: <repository-root> <output-directory> <profile> [consumers]");
            Generate(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2], args.Length == 3);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Generate(string root, string output, string profile, bool writeZig)
    {
        var declarationPath = Path.Combine(root, "src/Development/DesktopBuild/desktop.points.json");
        var bytes = File.ReadAllBytes(declarationPath);
        using var document = JsonDocument.Parse(bytes);
        var declaration = document.RootElement;
        if (!declaration.GetProperty("profiles").EnumerateArray().Any(item => item.GetString() == profile))
            throw new ArgumentException("Undeclared desktop profile: " + profile);
        var tree = declaration.GetProperty("tree").GetString()!;
        var points = ImmutableArray.CreateBuilder<PointDeclaration>();
        var values = new Dictionary<PointPath, PointValueSet>();
        var index = 0;
        foreach (var item in declaration.GetProperty("points").EnumerateArray())
        {
            var symbol = item.GetProperty("symbol").GetString()!;
            var path = new PointPath(PointPath.MainProgramPackage, 1, 0,
                PointFamily.Build, PointKind.Simple, item.GetProperty("id").GetUInt16());
            var isEnum = item.TryGetProperty("kind", out var kind) && kind.GetString() == "enum";
            var isString = kind.ValueKind == JsonValueKind.String && kind.GetString() == "string";
            var slot = isEnum ? new SlotContract
            {
                Kind = ValueKind.Enumeration,
                Enumeration = new EnumContract
                {
                    IsUnsigned = true, BitWidth = 8,
                    Members = item.GetProperty("members").EnumerateObject()
                        .Select(member => new EnumMemberContract(member.Name, member.Value.GetRawText()))
                        .ToImmutableArray()
                }
            } : isString ? new SlotContract { Kind = ValueKind.String } : new SlotContract
            {
                Kind = ValueKind.Integer, IntegerBitWidth = 32,
                IntegerMinimum = item.GetProperty("min").GetInt64(),
                IntegerMaximum = item.GetProperty("max").GetInt64()
            };
            points.Add(new PointDeclaration
            {
                Path = path, Name = tree + "." + symbol,
                Status = DeclarationStatus.Active, Update = UpdateClass.RebuildArtifact,
                ValueSource = "desktop.defaults", SourcePointer = "/points/" + index + "/default",
                Slots = [slot],
                // These are additional consumers of the existing points, with the same identity and value source.
                // Do not project unconsumed desktop choices into the window or browser.
                GenerationTargets = symbol switch
                {
                    "backend_timeout_ms" => [
                        new() { Language = GenerationLanguage.Zig, Form = GenerationForm.BuildConstant, Scope = "desktop", Symbol = symbol },
                        new() { Language = GenerationLanguage.CSharp, Form = GenerationForm.BuildConstant, Scope = "desktop", Symbol = symbol }],
                    "overlay_sample_interval_ms" => [
                        new() { Language = GenerationLanguage.Zig, Form = GenerationForm.BuildConstant, Scope = "desktop", Symbol = symbol },
                        new() { Language = GenerationLanguage.TypeScript, Form = GenerationForm.BuildConstant, Scope = "desktop", Symbol = symbol }],
                    _ => [new() { Language = GenerationLanguage.Zig, Form = GenerationForm.BuildConstant, Scope = "desktop", Symbol = symbol }]
                }
            });
            values.Add(path, PointValueSet.Simple(isEnum
                ? PointValue.FromEnumMember(item.GetProperty("default").GetString()!)
                : isString ? PointValue.FromString(item.GetProperty("default").GetString()!)
                : PointValue.FromInteger(item.GetProperty("default").GetInt64())));
            index++;
        }
        var registry = new PointRegistry(points.ToImmutable());
        var facts = FactResolver.Resolve(registry,
            new Dictionary<string, Func<PointDeclaration, PointValueSet>>
            { ["desktop.defaults"] = point => values[point.Path] }, []);
        var sourceSeal = Convert.ToHexString(SHA256.HashData(bytes));
        var toolSeal = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TypedProjection).Assembly.Location)));
        var projection = TypedProjection.Emit(registry, facts,
            new("desktop", "ResourceManager.Desktop", "DesktopPoints")
            { ZigStructureModule = "desktop_points_structure" }, sourceSeal, toolSeal,
            new Dictionary<string, ComplexDeclarationEmitter>(), new Dictionary<string, ComplexEmitter>());
        var ts = TypedProjection.EmitTypeScript(registry, facts,
            new("desktop", "ResourceManager.Desktop", "DesktopPoints") { TypeScriptStructureModuleSpecifier = "./desktopPoints.structure.ts" },
            sourceSeal, toolSeal, new Dictionary<string, TypeScriptComplexDeclarationEmitter>(), new Dictionary<string, TypeScriptComplexEmitter>());
        Directory.CreateDirectory(output);
        // The declaration structure is configuration independent and committed. Selected
        // values are build inputs outside the source tree, never another source of defaults.
        if (writeZig)
        {
            Write(Path.Combine(root, "src/UI/Desktop/src/generated/points.structure.zig"), projection.Zig.StructureSource);
            Write(Path.Combine(output, "points.values.zig"), projection.Zig.ValuesSource);
        }
        Write(Path.Combine(root, "src/UI/Core/Preferences/Generated/DesktopPoints.Structure.g.cs"), projection.CSharp.StructureSource);
        WriteSelected(Path.Combine(output, "DesktopPoints.Values.g.cs"), projection.CSharp.ValuesSource);
        Write(Path.Combine(root, "src/UI/Frontend/src/preferences/generated/desktopPoints.structure.ts"), ts.Sources.StructureSource);
        WriteSelected(Path.Combine(output, "desktopPoints.values.ts"), ts.Sources.ValuesSource);
        // TypeScript resolves this build input next to its declaration structure, like frontendPoints.values.ts.
        // This mirror is ignored by Git; the selected artifact is also retained in the registered owner.
        WriteSelected(Path.Combine(root, "src/UI/Frontend/src/preferences/generated/desktopPoints.values.ts"), ts.Sources.ValuesSource);
        var json = new JsonSerializerOptions { WriteIndented = true };
        Write(Path.Combine(output, "registry.json"), JsonSerializer.Serialize(registry.Points, json));
        Write(Path.Combine(output, "bindings.json"), JsonSerializer.Serialize(new
        { projection.SymbolBindings, projection.ZigMemberBindings, ts.MemberBindings, ts.ContractBindings }, json));
        Write(Path.Combine(output, "generation.json"), JsonSerializer.Serialize(new
        {
            tree, profile, points = points.Count, registry.ContractFingerprint, consumerProjectionVersion = 1,
            csharpValuesSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection.CSharp.ValuesSource))),
            typeScriptValuesSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ts.Sources.ValuesSource))),
            projection.ContentKey, typeScriptContentKey = ts.ContentKey, sourceSeal, toolSeal,
            structureModule = "desktop_points_structure", valuesModule = "desktop_points_values"
        }, json));
        Console.WriteLine($"Generated {points.Count} desktop freedom points with FreedomPoint dev.48: {output}");
    }

    private static void WriteSelected(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
            File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
