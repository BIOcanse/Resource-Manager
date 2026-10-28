using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FreedomPoint;
using FreedomPoint.Generators;
using FreedomPoint.SourceTracking;
using FreedomPoint.SourceTracking.TypeScript;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ResourceManager.FrontendPreferencesBuild;

internal static class PreferenceSourceCheck
{
    internal static void Run(PointRegistry registry, TypeScriptProjection projection,
        string root, string output, string compilerContextPath, string typeScriptContextPath)
    {
        using var compiler = JsonDocument.Parse(File.ReadAllBytes(compilerContextPath));
        using var typescript = JsonDocument.Parse(File.ReadAllBytes(typeScriptContextPath));
        var ts = typescript.RootElement;
        var dependencyFiles = ts.GetProperty("contextFiles").EnumerateArray().Select(item => "frontend-dependencies/" + item.GetString()).ToImmutableArray();
        const string generatedPath = "frontend/src/preferences/generated/frontendPoints.values.ts";
        const string mapPath = "analysis/typescript-symbols.json";
        var map = new TypeScriptGeneratedSymbolMap(1, projection.MemberBindings.Select(binding =>
            new TypeScriptGeneratedSymbol(binding.Point, generatedPath,
                Hash(Path.Combine(root, "src/UI/Frontend/src/preferences/generated/frontendPoints.values.ts")), binding.Symbol)).ToImmutableArray());
        File.WriteAllText(Path.Combine(output, "typescript-symbols.json"), JsonSerializer.Serialize(map, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var excludes = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", "node_modules", "dist");
        var dependencyRoot = ts.GetProperty("dependencyRoot").GetString()!;
        // Enumerate only the root itself; dependency content comes from the real
        // compiler's explicit file list, never from a walk through node_modules.
        var excludedPackages = Directory.EnumerateDirectories(dependencyRoot).Select(Path.GetFileName)
            .Select(name => name!).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var scan = new SourceScanOptions(Path.Combine(root, "src/UI/Core"), ["."], excludes, 20000, 32 * 1024 * 1024)
        {
            RootIdentity = "native-shell",
            AdditionalRoots = [
                new("frontend", Path.Combine(root, "src/UI/Frontend"), ["src"], excludes),
                new("frontend-dependencies", dependencyRoot, ["."], excludedPackages),
                new("managed-generated", compiler.RootElement.GetProperty("artifactsPath").GetString()!, ["obj/ResourceManager.NativeUi", "freedom-point-generated/ResourceManager.NativeUi"], ImmutableHashSet<string>.Empty),
                new("analysis", output, ["."], Directory.EnumerateDirectories(output).Select(Path.GetFileName).Select(name => name!).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase))
            ],
            ExplicitFiles = dependencyFiles.AddRange(new[] { "frontend/tsconfig.json", "frontend/package-lock.json", "frontend/src/i18n/managedDependencyAcknowledgements.json", "frontend/preferences.points.json", "analysis/frontend.tsconfig.json", mapPath, "native-shell/ResourceManager.NativeUi.csproj" })
        };
        var captured = SourceCapture.Capture(scan);
        var contexts = compiler.RootElement.GetProperty("projects").EnumerateArray().Select(ReadContext).ToImmutableArray();
        var cs = CSharpSourceAnalyzer.AnalyzeReferences(registry, captured, Classify, [],
            new([], string.Empty) { CompilationContexts = contexts });
        var tsContext = new TypeScriptAnalysisContext(ts.GetProperty("nodeExecutable").GetString()!, ts.GetProperty("nodeVersion").GetString()!,
            ts.GetProperty("typeScriptModule").GetString()!, ts.GetProperty("typeScriptVersion").GetString()!,
            "analysis/frontend.tsconfig.json", mapPath, Hash(Path.Combine(output, "typescript-symbols.json")),
            120000, 64 * 1024 * 1024, 32 * 1024 * 1024)
        {
            CompilerContextRelativePaths = dependencyFiles.AddRange(new[] { "frontend/package-lock.json", "frontend/src/i18n/managedDependencyAcknowledgements.json" })
        };
        var tsAnalysis = TypeScriptSourceAnalyzer.AnalyzeReferences(registry, captured, Classify, tsContext);
        var declarationText = File.ReadAllText(Path.Combine(root, "src/UI/Frontend/preferences.points.json"));
        var declarations = registry.Points.Where(point => registry.RequiresOwnReader(point.Path)).Select(point =>
        {
            var symbol = JsonSerializer.Serialize(point.GenerationTargets[0].Symbol);
            var start = declarationText.IndexOf(symbol, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("Missing actual declaration span: " + point.Name);
            var line = declarationText.AsSpan(0, start).Count('\n') + 1;
            return new SourceBinding(point.Path, "frontend/preferences.points.json", point.Name, line, line, start, symbol.Length, SourceRole.Declaration);
        }).ToImmutableArray();
        var result = SourceAnalyzer.Complete(registry, captured, [cs, tsAnalysis, new(declarations, [])]);
        var issues = result.Issues.AddRange(result.Index is null ? [] : SourceCapture.VerifyCurrent(result.Index, SourceCapture.Capture(scan)));
        File.WriteAllText(Path.Combine(output, "extension-source-result.json"), JsonSerializer.Serialize(new
        {
            passed = result.IsComplete && issues.IsEmpty, result.IsComplete, result.Failure, issues,
            ownPoints = registry.Points.Count(point => registry.RequiresOwnReader(point.Path)),
            files = result.Index?.Files.Length, bindings = result.Index?.Bindings
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        foreach (var issue in issues) Console.Error.WriteLine($"{issue.Code}: {issue.RelativePath}: {issue.Detail}");
        if (!result.IsComplete || !issues.IsEmpty) throw new InvalidDataException(result.Failure ?? "Extension source contract failed.");
        Console.WriteLine("SDK46 extension source check complete: zero issues.");

        CSharpCompilationContext ReadContext(JsonElement project)
        {
            var properties = project.GetProperty("Properties");
            var items = project.GetProperty("Items");
            var selected = items.GetProperty("Compile").EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!)
                .Concat(GeneratedFiles(properties.GetProperty("CompilerGeneratedFilesOutputPath").GetString()))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new CSharpCompilationContext
            {
                ProjectRelativePath = CapturedPath(Path.Combine(root, project.GetProperty("projectRelativePath").GetString()!)),
                Configuration = properties.GetProperty("Configuration").GetString()!, TargetFramework = properties.GetProperty("TargetFramework").GetString()!,
                AssemblyName = properties.GetProperty("AssemblyName").GetString()!,
                CompileItems = selected.Select(path => new CSharpCompileItem(CapturedPath(path), Hash(path))).ToImmutableArray(),
                GlobalUsingsSource = string.Empty, PreprocessorSymbols = properties.GetProperty("DefineConstants").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray(),
                ParseLanguageVersion = LanguageVersionFacts.TryParse(properties.GetProperty("LangVersion").GetString(), out var language) ? language : throw new InvalidDataException("Invalid C# LangVersion"),
                OutputKind = properties.GetProperty("OutputType").GetString() switch { "WinExe" => OutputKind.WindowsApplication, "Exe" => OutputKind.ConsoleApplication, _ => OutputKind.DynamicallyLinkedLibrary },
                NullableContextOptions = properties.GetProperty("Nullable").GetString() == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Disable,
                AllowUnsafe = bool.Parse(properties.GetProperty("AllowUnsafeBlocks").GetString()!), ProjectReferences = [],
                References = items.GetProperty("ReferencePath").EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!)
                    .Select(path => new CSharpCompilationReference(path, AssemblyName.GetAssemblyName(path).FullName!, Hash(path))).ToImmutableArray()
            };
        }
        string CapturedPath(string path) => captured.TryGetRelativePath(path, out var relative)
            ? relative : throw new InvalidDataException("Uncaptured C# compiler input: " + path);
    }

    private static IEnumerable<string> GeneratedFiles(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        var directories = new Stack<string>(); directories.Push(root);
        var count = 0;
        while (directories.TryPop(out var directory))
        {
            if (++count > 4096) throw new InvalidDataException("Generated source directory limit exceeded.");
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Generated source root contains a junction.");
                directories.Push(child);
            }
        }
    }
    private static SourceRole Classify(string path) => path.Contains("/Preferences/Generated/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("managed-generated/", StringComparison.Ordinal) || path.StartsWith("analysis/", StringComparison.Ordinal)
        || path.Contains("/preferences/generated/", StringComparison.Ordinal) ? SourceRole.Generated : SourceRole.Production;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
