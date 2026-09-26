namespace Resource_Manager_APP.Tests;

public sealed class RetiredSelfOptimizationControlPlaneTests
{
    [Fact]
    public void ProductionSourcesContainOnlyHostManagerSelfControlRoutes()
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
            Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedPath(path))
                .Where(static path => !path.EndsWith(
                    Path.Combine("Application", "Settings", "AppSettingsMigrator.cs"),
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("CompiledSelfOptimizationPlan", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSelfOptimizationSettings", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/self-optimization/", backendSource, StringComparison.Ordinal);
        Assert.Contains("/api/optimization/smart/run-once", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adapters/resource-manager/ledger", backendSource, StringComparison.Ordinal);
        Assert.Contains("/api/adapters/resource-manager/scheduling", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adapters/resource-manager/visible-regions", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceManagerSelfResourceMarker", backendSource, StringComparison.Ordinal);

        var clientSourceRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "..", "src", "UI", "Frontend", "src"));
        var clientSource = string.Join(
            '\n',
            Directory.EnumerateFiles(clientSourceRoot, "*.*", SearchOption.AllDirectories)
                .Where(static path =>
                    path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("AppSelfOptimizationSettings", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("selfOptimization", clientSource, StringComparison.Ordinal);
    }

    private static bool IsGeneratedPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(static segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "wwwroot", StringComparison.OrdinalIgnoreCase));
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
