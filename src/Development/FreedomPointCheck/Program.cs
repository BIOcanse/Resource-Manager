using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreedomPoint;
using FreedomPoint.SourceTracking;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] is not ("--create-partition" or "--refresh-partition-contract"))
{
    Console.Error.WriteLine("Usage: FreedomPointCheck <repository> <compiler-context.json> <output-directory> [--create-partition|--refresh-partition-contract]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[2]);
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};
try
{
    Directory.CreateDirectory(output);
    WriteResult(new { passed = false, status = "checking" });
    var host = FreedomPointRegistry.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/FreedomPoints/backend.json")));
    var registry = host.Sdk.Registry;
    var issues = new List<VerificationIssue>();
    var bindingFile = Path.Combine(root, "src/FreedomPointContracts/Domain/BackendFreedomPointPositions.cs");
    var generated = GeneratePositions(host.Declarations);
    File.WriteAllText(Path.Combine(output, "BackendFreedomPointPositions.cs"), generated);
    if (File.ReadAllText(bindingFile).Replace("\r\n", "\n") != generated)
        issues.Add(new("generated_positions_stale", null, Path.GetRelativePath(root, bindingFile), "Regenerate the position constants before compiling consumers."));

    var partitionFile = Path.Combine(root, "src/Core/Configuration/FreedomPoints/backend.groups.json");
    if (args.Length == 4 && args[3] == "--create-partition")
    {
        if (File.Exists(partitionFile)) throw new InvalidDataException("Custom partition already exists; preserve or explicitly edit its grouping data.");
        File.WriteAllText(partitionFile, JsonSerializer.Serialize(CreatePartition(host.Declarations), json));
    }
    var partitionData = JsonSerializer.Deserialize<PartitionDocument>(File.ReadAllBytes(partitionFile), json)
        ?? throw new InvalidDataException("Custom partition is empty.");
    if (args.Length == 4 && args[3] == "--refresh-partition-contract")
    {
        // Validate the existing groups against the new declarations before changing only their contract.
        _ = new CustomPartition(registry, registry.ContractFingerprint, partitionData.Groups);
        var partitionNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(partitionFile))!.AsObject();
        partitionNode["contractFingerprint"] = registry.ContractFingerprint;
        File.WriteAllText(partitionFile, partitionNode.ToJsonString(json) + "\n");
        partitionData = partitionData with { ContractFingerprint = registry.ContractFingerprint };
    }
    var partition = new CustomPartition(registry, partitionData.ContractFingerprint, partitionData.Groups);

    using var compiler = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    var evaluated = compiler.RootElement.GetProperty("projects").EnumerateArray().ToArray();
    const string appProjectRelativePath = "src/Core/ResourceManager.App.csproj";
    var appProject = evaluated.SingleOrDefault(project => string.Equals(
        project.GetProperty("projectRelativePath").GetString(), appProjectRelativePath, StringComparison.Ordinal));
    if (appProject.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        throw new InvalidDataException("The evaluated ResourceManager.App compiler context is missing.");
    var appProperties = appProject.GetProperty("Properties");
    var appIntermediateOutputPath = appProperties.GetProperty("IntermediateOutputPath").GetString();
    if (string.IsNullOrWhiteSpace(appIntermediateOutputPath))
        throw new InvalidDataException("The evaluated app compiler context has no IntermediateOutputPath.");
    if (!Path.IsPathFullyQualified(appIntermediateOutputPath))
        appIntermediateOutputPath = Path.Combine(Path.GetDirectoryName(Path.Combine(root, appProjectRelativePath))!,
            appIntermediateOutputPath);
    var stateItemsSource = Path.GetFullPath(Path.Combine(appIntermediateOutputPath, "StateItems.g.cs"));
    var artifactsRoot = Path.GetFullPath(compiler.RootElement.GetProperty("artifactsPath").GetString()!);
    var stateItemsRelativePath = Path.GetRelativePath(artifactsRoot, stateItemsSource);
    if (Path.IsPathRooted(stateItemsRelativePath) || stateItemsRelativePath == ".." ||
        stateItemsRelativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        stateItemsRelativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("The generated app StateItems source is outside the task artifacts root.");
    if (!File.Exists(stateItemsSource))
        throw new InvalidDataException("The compiler-generated app StateItems source is missing: " + stateItemsSource);
    // This target adds StateItems.g.cs before CoreCompile, so the explicit MSBuild
    // Compile item query does not include it. Capture that exact generated source.
    var additionalSources = ImmutableArray.Create("artifacts/" + stateItemsRelativePath.Replace('\\', '/'));
    var ledgerRoots = evaluated.Select(project => project.GetProperty("Properties")
            .TryGetProperty("ResourceManagerLedgerOutputRoot", out var value) ? value.GetString() : null)
        .Where(static path => !string.IsNullOrWhiteSpace(path)).Select(static path => Path.GetFullPath(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (ledgerRoots.Length > 1) throw new InvalidDataException("Multiple product Ledger generated roots were evaluated.");
    var additionalRoots = ImmutableArray.CreateBuilder<SourceRootOptions>();
    additionalRoots.Add(new("artifacts", compiler.RootElement.GetProperty("artifactsPath").GetString()!,
        ["obj", "freedom-point-generated"], ImmutableHashSet<string>.Empty));
    if (ledgerRoots.Length == 1)
    {
        // Capture the small paired consumer output, including the two actual
        // Compile items, without scanning its task's tools, logs or caches.
        additionalRoots.Add(new("ledger-generated", ledgerRoots[0], ["."], ImmutableHashSet<string>.Empty));
    }
    var scan = new SourceScanOptions(root, ["src/Core", "src/FreedomPointContracts", "src/ReadWriteCore", "src/SubscriptionCore"],
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", "third_party", "node_modules", ".zig-cache", "zig-out", "Config", "UserData"),
        MaximumFiles: 20_000, MaximumFileBytes: 32 * 1024 * 1024)
    {
        AdditionalRoots = additionalRoots.ToImmutable(),
        ExplicitFiles = additionalSources
    };
    var current = SourceCapture.Capture(scan);
    var projectAssemblyNames = evaluated.Select(project => project.GetProperty("Properties").GetProperty("AssemblyName").GetString()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var contexts = evaluated.Select(ReadContext).ToImmutableArray();
    var analysis = CSharpSourceAnalyzer.Analyze(registry, current, Classify, [],
        new([], string.Empty) { CompilationContexts = contexts });
    issues.AddRange(analysis.Issues);
    if (!analysis.IsComplete || analysis.Index is null)
    {
        WriteResult(new { passed = false, status = "incomplete", analysis.Failure, issues });
        foreach (var issue in issues) Console.Error.WriteLine($"{issue.Code} {issue.RelativePath} {issue.Detail}");
        return 1;
    }
    // Ensure a file changed/deleted while the analyzer was working cannot produce a current success.
    issues.AddRange(SourceCapture.VerifyCurrent(analysis.Index, SourceCapture.Capture(scan)));
    var report = new
    {
        passed = issues.Count == 0,
        status = issues.Count == 0 ? "complete" : "violations",
        sdkVersion = typeof(PointPath).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        registry.ContractFingerprint,
        declarations = registry.Points.Length,
        active = registry.Points.Count(static point => point.Status == DeclarationStatus.Active),
        pending = registry.Points.Count(static point => point.Status == DeclarationStatus.Pending),
        productionPoints = analysis.Index.Bindings.Where(static binding => binding.Owner == SourceOwner.Consumer && binding.Role == SourceRole.Production).Select(static binding => binding.Point).Distinct().Count(),
        issues,
        index = new { analysis.Index.Files, analysis.Index.Bindings },
        fileView = analysis.Index.BuildFileView(),
        customView = partition.Groups,
        sourceLookup = registry.Points.Where(static p => p.Status == DeclarationStatus.Active)
            .Select(static p => new { p.Path, p.ValueSource, p.SourcePointer })
    };
    var serialized = JsonSerializer.Serialize(report, json);
    var temporaryIndex = Path.Combine(output, "source-index.pending.json");
    File.WriteAllText(temporaryIndex, serialized);
    File.Move(temporaryIndex, Path.Combine(output, "source-index.json"), overwrite: true);
    WriteResult(report);
    Console.WriteLine($"FreedomPoint: {registry.Points.Length} declarations, {report.productionPoints}/{report.active} production points, {report.pending} pending, {issues.Count} issues.");
    foreach (var issue in issues) Console.Error.WriteLine($"{issue.Code} {issue.Point} {issue.RelativePath} {issue.Detail}");
    return issues.Count == 0 ? 0 : 1;

    CSharpCompilationContext ReadContext(JsonElement project)
    {
        var properties = project.GetProperty("Properties");
        var items = project.GetProperty("Items");
        var compileFiles = items.GetProperty("Compile").EnumerateArray()
            .Select(static item => item.GetProperty("FullPath").GetString()!).ToArray();
        var projectPath = project.GetProperty("projectRelativePath").GetString()!;
        var generatedRoot = properties.GetProperty("CompilerGeneratedFilesOutputPath").GetString()!;
        var generatedFiles = Directory.Exists(generatedRoot)
            ? Directory.EnumerateFiles(generatedRoot, "*.cs", SearchOption.AllDirectories) : [];
        var additionalCompileFiles = string.Equals(projectPath, appProjectRelativePath, StringComparison.Ordinal)
            ? new[] { stateItemsSource }
            : Array.Empty<string>();
        var selected = compileFiles.Concat(generatedFiles).Concat(additionalCompileFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Any(path => !current.TryGetRelativePath(path, out _)))
            throw new InvalidDataException($"Uncaptured Compile items in {projectPath}.");
        return new CSharpCompilationContext
        {
            ProjectRelativePath = CapturedPath(Path.Combine(root, projectPath)),
            Configuration = properties.GetProperty("Configuration").GetString()!,
            TargetFramework = properties.GetProperty("TargetFramework").GetString()!,
            AssemblyName = properties.GetProperty("AssemblyName").GetString()!,
            CompileItems = selected.Select(path => new CSharpCompileItem(CapturedPath(path), Hash(path))).ToImmutableArray(),
            GlobalUsingsSource = string.Empty,
            PreprocessorSymbols = properties.GetProperty("DefineConstants").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray(),
            ParseLanguageVersion = ParseLanguage(properties.GetProperty("LangVersion").GetString()!),
            OutputKind = properties.GetProperty("OutputType").GetString() == "Exe" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
            NullableContextOptions = properties.GetProperty("Nullable").GetString() == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Disable,
            AllowUnsafe = bool.Parse(properties.GetProperty("AllowUnsafeBlocks").GetString()!),
            ProjectReferences = items.GetProperty("ProjectReference").EnumerateArray()
                .Select(static item => item.GetProperty("FullPath").GetString()!)
                .Where(path => evaluated.Any(candidate => Path.GetFullPath(Path.Combine(root, candidate.GetProperty("projectRelativePath").GetString()!)) == path))
                .Select(path =>
                {
                    var referenced = evaluated.Single(candidate => Path.GetFullPath(Path.Combine(root, candidate.GetProperty("projectRelativePath").GetString()!)) == path).GetProperty("Properties");
                    return new CSharpProjectReference(CapturedPath(path), referenced.GetProperty("Configuration").GetString()!, referenced.GetProperty("TargetFramework").GetString()!);
                }).ToImmutableArray(),
            References = items.GetProperty("ReferencePath").EnumerateArray().Select(static item => item.GetProperty("FullPath").GetString()!)
                .Where(path => !projectAssemblyNames.Contains(AssemblyName.GetAssemblyName(path).Name!))
                .Select(path => new CSharpCompilationReference(path, AssemblyName.GetAssemblyName(path).FullName!, Hash(path))).ToImmutableArray()
        };
    }
    string CapturedPath(string physicalPath) => current.TryGetRelativePath(physicalPath, out var relativePath)
        ? relativePath : throw new InvalidDataException($"Compiler input was not captured: {physicalPath}");
}
catch (Exception exception) when (exception is FreedomPointException or InvalidDataException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
{
    var failure = JsonSerializer.Serialize(new { passed = false, error = exception.GetType().Name, detail = exception.Message }, json);
    File.WriteAllText(Path.Combine(output, "source-result.json"), failure);
    File.WriteAllText(Path.Combine(output, "failure.json"), failure);
    Console.Error.WriteLine(exception.ToString());
    return 1;
}

void WriteResult(object result) => File.WriteAllText(Path.Combine(output, "source-result.json"), JsonSerializer.Serialize(result, json));
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
static LanguageVersion ParseLanguage(string text) => LanguageVersionFacts.TryParse(text, out var version)
    ? version : throw new InvalidDataException($"Invalid evaluated LangVersion '{text}'.");

static SourceRole Classify(string path)
{
    if (path.StartsWith("artifacts/", StringComparison.Ordinal)) return SourceRole.Compiler;
    if (path.StartsWith("ledger-generated/", StringComparison.Ordinal)) return SourceRole.Compiler;
    if (path.StartsWith("source/", StringComparison.Ordinal)) path = path[7..];
    if (path.StartsWith("src/FreedomPointContracts/", StringComparison.Ordinal)) return SourceRole.Compiler;
    if (path.StartsWith("src/Core/Domain/RuntimeSpecialization/", StringComparison.Ordinal)) return SourceRole.Compiler;
    if (path.StartsWith("src/Core/Infrastructure/RuntimeSpecialization/", StringComparison.Ordinal)) return SourceRole.Compiler;
    return SourceRole.Production;
}

static string GeneratePositions(CompiledFreedomPointTree tree)
{
    var text = new StringBuilder("// Generated from the host declaration positions. FreedomPointCheck verifies this file.\nnamespace ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;\n\ninternal static class BackendFreedomPointPositions\n{\n");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var point in tree.EnumeratePoints())
    {
        var name = string.Concat(point.Id.Split('_').Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (!seen.Add(name)) throw new InvalidDataException("Host binding names collide; supply distinct generated member names.");
        text.Append("    public const ulong ").Append(name).Append(" = 0x").Append(point.Path).Append("UL;\n");
    }
    return text.Append("}\n").ToString();
}

static PartitionDocument CreatePartition(CompiledFreedomPointTree tree)
{
    var groups = new List<CustomGroup>();
    var visit = new Stack<(CompiledFreedomPointTable Table, string? Parent, int Order)>();
    for (var index = tree.Children.Length - 1; index >= 0; index--) visit.Push((tree.Children[index], null, index));
    while (visit.TryPop(out var item))
    {
        var key = (item.Parent is null ? tree.Namespace : item.Parent) + "/" + item.Table.Id;
        var points = item.Table.Build.Simple.Concat(item.Table.Build.Complex).Concat(item.Table.Runtime.Simple).Concat(item.Table.Runtime.Complex);
        groups.Add(new(key, item.Parent, item.Table.Id, item.Table.Description, item.Order, points.Select(static point => point.Path).ToImmutableArray()));
        for (var index = item.Table.Children.Length - 1; index >= 0; index--)
            visit.Push((item.Table.Children[index], key, index));
    }
    return new(tree.SdkContractFingerprint, groups.ToArray());
}

internal sealed record PartitionDocument(string ContractFingerprint, CustomGroup[] Groups);
