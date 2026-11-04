using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreedomPoint;
using FreedomPoint.Generators;
using ResourceManager.Ledger.Configuration;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    var command = args[0];
    if (command == "check")
    {
        var options = ReadOptions(args.Skip(1));
        var points = new LedgerPoints(options.TableCount);
        IReadOnlyList<FactOverride> checkOverrides = options.ChoicesPath is { } checkChoicesPath
            ? ReadOverrides(checkChoicesPath, points)
            : Array.Empty<FactOverride>();
        var facts = points.Check(checkOverrides);
        Console.WriteLine($"Ledger build contracts passed for {points.TableCount} tables.");
        Console.WriteLine("Configuration fingerprint: " + facts.ContentFingerprint);
        return 0;
    }

    if (command == "structure")
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 2;
        }
        var firstDirectory = RequireAbsoluteDirectory(args[1]);
        var options = ReadOptions(args.Skip(2));
        var points = new LedgerPoints(options.TableCount);
        var facts = points.Resolve([]);
        var projection = points.Emit(facts, SourceSeal(), ToolchainSeal());
        WriteStructure(firstDirectory, points, projection);
        Console.WriteLine($"Generated FreedomPoint structure for {points.TableCount} tables: {firstDirectory}");
        return 0;
    }

    if (command != "values" || args.Length < 3)
    {
        PrintUsage();
        return 2;
    }

    var valuesDirectory = RequireAbsoluteDirectory(args[1]);
    var structureDirectory = RequireAbsoluteDirectory(args[2]);
    if (PathEquals(valuesDirectory, structureDirectory))
        throw new ArgumentException("Build values and checked-in structure must use separate directories.");

    var valueOptions = ReadOptions(args.Skip(3));
    var ledgerPoints = new LedgerPoints(valueOptions.TableCount);
    var defaults = ledgerPoints.Resolve([]);
    var defaultProjection = ledgerPoints.Emit(defaults, SourceSeal(), ToolchainSeal());
    IReadOnlyList<FactOverride> overrides = valueOptions.ChoicesPath is { } valuesChoicesPath
        ? ReadOverrides(valuesChoicesPath, ledgerPoints)
        : Array.Empty<FactOverride>();
    var selectedFacts = ledgerPoints.Resolve(overrides);
    var selectedProjection = ledgerPoints.Emit(selectedFacts, SourceSeal(), ToolchainSeal());
    VerifyStructure(structureDirectory, ledgerPoints, defaultProjection);
    WriteValues(valuesDirectory, selectedProjection, selectedFacts);
    Console.WriteLine($"Generated selected FreedomPoint values for {ledgerPoints.TableCount} tables: {valuesDirectory}");
    Console.WriteLine("Configuration fingerprint: " + selectedFacts.ContentFingerprint);
    return 0;
}
catch (FreedomPointException exception)
{
    Console.Error.WriteLine($"FreedomPoint configuration rejected ({exception.Code}): {exception.Message}");
    return 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  Ledger.Configuration structure <structure-directory> [--table-count N]");
    Console.Error.WriteLine("  Ledger.Configuration values <build-values-directory> <structure-directory> [--table-count N] [--choices choices.json]");
    Console.Error.WriteLine("  Ledger.Configuration check [--table-count N] [--choices choices.json]");
}

static string RequireAbsoluteDirectory(string path)
{
    if (!Path.IsPathFullyQualified(path))
        throw new ArgumentException("Output paths must be absolute: " + path);
    return Path.GetFullPath(path);
}

static bool PathEquals(string left, string right)
    => string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

static (int TableCount, string? ChoicesPath) ReadOptions(IEnumerable<string> arguments)
{
    var tableCount = 1;
    string? choicesPath = null;
    var seenTableCount = false;
    var seenChoices = false;
    using var iterator = arguments.GetEnumerator();
    while (iterator.MoveNext())
    {
        switch (iterator.Current)
        {
            case "--table-count":
                if (seenTableCount || !iterator.MoveNext() ||
                    !int.TryParse(iterator.Current, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out tableCount))
                    throw new ArgumentException("--table-count must be specified once with a positive integer.");
                seenTableCount = true;
                break;
            case "--choices":
                if (seenChoices || !iterator.MoveNext())
                    throw new ArgumentException("--choices must be specified once with a file path.");
                choicesPath = Path.GetFullPath(iterator.Current);
                seenChoices = true;
                break;
            default:
                throw new ArgumentException("Unknown configuration option: " + iterator.Current);
        }
    }
    if (tableCount < 1)
        throw new ArgumentException("--table-count must be a positive integer.");
    return (tableCount, choicesPath);
}

static IReadOnlyList<FactOverride> ReadOverrides(string path, LedgerPoints points)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    if (document.RootElement.ValueKind != JsonValueKind.Object)
        throw new ArgumentException("The choices file must contain a JSON object.");

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var overrides = new List<FactOverride>();
    foreach (var property in document.RootElement.EnumerateObject())
    {
        if (!seen.Add(property.Name))
            throw new ArgumentException("A choice may appear only once: " + property.Name);
        overrides.Add(points.Override(property.Name, property.Value));
    }
    return overrides;
}

static void WriteStructure(string directory, LedgerPoints points, Projection projection)
{
    Directory.CreateDirectory(directory);
    WriteIfChanged(Path.Combine(directory, "LedgerDefaults.g.cs"), projection.CSharp.StructureSource);
    WriteIfChanged(Path.Combine(directory, "ledger_structure.zig"), projection.Zig.StructureSource);
    WriteIfChanged(Path.Combine(directory, "ledger_registry.json"),
        JsonSerializer.Serialize(points.Registry.Points, JsonOptions()));
    WriteIfChanged(Path.Combine(directory, "ledger_zig_bindings.json"),
        JsonSerializer.Serialize(projection.ZigMemberBindings, JsonOptions()));
}

static void VerifyStructure(string directory, LedgerPoints points, Projection projection)
{
    var expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LedgerDefaults.g.cs"] = projection.CSharp.StructureSource,
        ["ledger_structure.zig"] = projection.Zig.StructureSource,
        ["ledger_registry.json"] = JsonSerializer.Serialize(points.Registry.Points, JsonOptions()),
        ["ledger_zig_bindings.json"] = JsonSerializer.Serialize(projection.ZigMemberBindings, JsonOptions())
    };
    foreach (var (name, contents) in expected)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(EncodingBytes(contents)))
            throw new InvalidDataException("Checked-in SDK structure differs from the FreedomPoint projection: " + path);
    }
}

static void WriteValues(string directory, Projection projection, ResolvedFacts facts)
{
    Directory.CreateDirectory(directory);
    WriteIfChanged(Path.Combine(directory, "LedgerDefaults.Values.g.cs"), projection.CSharp.ValuesSource);
    WriteIfChanged(Path.Combine(directory, "ledger_values.zig"), projection.Zig.ValuesSource);
    WriteIfChanged(Path.Combine(directory, "ledger_facts.json"),
        JsonSerializer.Serialize(facts.Items, JsonOptions()));
}

static void WriteIfChanged(string path, string contents)
{
    var bytes = EncodingBytes(contents);
    if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
}

static byte[] EncodingBytes(string contents) => new UTF8Encoding(false).GetBytes(contents);

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

static string SourceSeal()
    => HashAssembly(Assembly.GetExecutingAssembly());

static string ToolchainSeal()
    => HashAssembly(typeof(TypedProjection).Assembly);

static string HashAssembly(Assembly assembly)
{
    if (string.IsNullOrWhiteSpace(assembly.Location))
        throw new InvalidOperationException("Cannot seal an in-memory assembly: " + assembly.FullName);
    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
}
