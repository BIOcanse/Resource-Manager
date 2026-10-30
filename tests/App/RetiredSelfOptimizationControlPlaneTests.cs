namespace Resource_Manager_APP.Tests;

public sealed class RetiredSelfOptimizationControlPlaneTests
{
    [Fact]
    public void ProductionSourcesContainOnlyCentralOptimizationRoutes()
    {
        var appRoot = FindAppRoot();
        var retiredFiles = new[]
        {
            "Application/SelfOptimization/IResourceManagerSelfOptimizationStatusService.cs",
            "Domain/RuntimeSpecialization/CompiledSelfOptimizationPlan.cs",
            "Domain/SelfOptimization/ResourceManagerSelfOptimization.cs",
            "Infrastructure/SelfOptimization/ResourceManagerSelfOptimizationStatusService.cs"
        };
        foreach (var retiredFile in retiredFiles)
        {
            Assert.False(
                File.Exists(Path.Combine(appRoot, retiredFile.Replace('/', Path.DirectorySeparatorChar))),
                retiredFile);
        }

        var backendSource = string.Join(
            '\n',
            EnumerateSourceFiles(appRoot, "*.cs")
                .Concat(EnumerateSourceFiles(Path.GetFullPath(Path.Combine(appRoot, "..", "FreedomPointContracts")), "*.cs"))
                .Where(static path => !path.EndsWith(
                    Path.Combine("Application", "Settings", "AppSettingsMigrator.cs"),
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("CompiledSelfOptimizationPlan", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSelfOptimizationSettings", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/self-optimization/", backendSource, StringComparison.Ordinal);
        Assert.Contains("/api/optimization/smart/run-once", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adapters/resource-manager/ledger", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adapters/resource-manager/scheduling", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adapters/resource-manager/visible-regions", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceManagerSelfResourceMarker", backendSource, StringComparison.Ordinal);

        var clientSourceRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "..", "src", "UI", "Frontend", "src"));
        var clientSource = string.Join(
            '\n',
            EnumerateSourceFiles(clientSourceRoot, "*.*")
                .Where(static path =>
                    path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("AppSelfOptimizationSettings", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("selfOptimization", clientSource, StringComparison.Ordinal);
    }

    // Prune build output before descending. Committed Generated/generated source remains covered.
    private static IEnumerable<string> EnumerateSourceFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (!visited.Add(directory)) continue;
            if (visited.Count > 20_000)
                throw new InvalidOperationException("Production source scan exceeded its traversal bound.");
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                if (++files > 100_000)
                    throw new InvalidOperationException("Production source scan exceeded its file bound.");
                yield return path;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("dist", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".zig-cache", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("zig-out", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }
    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var sourceCandidate = Path.GetFullPath(Path.Combine(
                sourceDirectory,
                "..",
                "..",
                "src",
                "Core"));
            if (File.Exists(Path.Combine(sourceCandidate, "ResourceManager.App.csproj")))
            {
                return sourceCandidate;
            }
        }

        foreach (var seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(seed);
            var remainingParents = 32;
            while (current is not null && remainingParents-- > 0)
            {
                var candidate = Path.Combine(current.FullName, "src", "Core");
                if (File.Exists(Path.Combine(candidate, "ResourceManager.App.csproj")))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Resource Manager-APP source root was not found.");
    }
}
