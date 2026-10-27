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
            if (args.Length != 3) throw new ArgumentException("Usage: <repository-root> <output-directory> <profile>");
            Generate(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Generate(string root, string output, string profile)
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
            } : new SlotContract
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
                GenerationTargets = [new()
                {
                    Language = GenerationLanguage.Zig, Form = GenerationForm.BuildConstant,
                    Scope = "desktop", Symbol = symbol
                }]
            });
            values.Add(path, PointValueSet.Simple(isEnum
                ? PointValue.FromEnumMember(item.GetProperty("default").GetString()!)
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
        Directory.CreateDirectory(output);
        // The declaration structure is configuration independent and committed. Selected
        // values are build inputs outside the source tree, never another source of defaults.
        Write(Path.Combine(root, "src/UI/Desktop/src/generated/points.structure.zig"), projection.Zig.StructureSource);
        Write(Path.Combine(output, "points.values.zig"), projection.Zig.ValuesSource);
        var json = new JsonSerializerOptions { WriteIndented = true };
        Write(Path.Combine(output, "registry.json"), JsonSerializer.Serialize(registry.Points, json));
        Write(Path.Combine(output, "bindings.json"), JsonSerializer.Serialize(new
        { projection.SymbolBindings, projection.ZigMemberBindings }, json));
        Write(Path.Combine(output, "generation.json"), JsonSerializer.Serialize(new
        {
            tree, profile, points = points.Count, registry.ContractFingerprint,
            projection.ContentKey, sourceSeal, toolSeal,
            structureModule = "desktop_points_structure", valuesModule = "desktop_points_values"
        }, json));
        Console.WriteLine($"Generated {points.Count} desktop freedom points with FreedomPoint dev.45: {output}");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
            File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
