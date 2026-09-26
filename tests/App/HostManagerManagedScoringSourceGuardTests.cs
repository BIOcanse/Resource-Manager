namespace Resource_Manager_APP.Tests;

public sealed class HostManagerManagedScoringSourceGuardTests
{
    [Fact]
    public void WelfareIsOwnedByNativeComputeScoringAndSmartConsumesOnlyFinalScores()
    {
        var appRoot = FindAppRoot();
        var nativeSourceRoot = Path.Combine(appRoot, "NativeCore", "src");
        var processScores = File.ReadAllText(Path.Combine(
            nativeSourceRoot,
            "scheduling",
            "compute_scoring",
            "process_scores.zig"));
        var smartPlanner = File.ReadAllText(Path.Combine(
            nativeSourceRoot,
            "scheduling",
            "smart_coordinator",
            "planner.zig"));
        var smartProtocol = File.ReadAllText(Path.Combine(
            nativeSourceRoot,
            "scheduling",
            "smart_coordinator",
            "protocol.zig"));
        var managedSmartAbi = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "NativeCore",
            "NativeSmartCoordinatorAbi.cs"));
        var managedWorkspace = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "ComputeScoring",
            "HostManagerComputeScoringWorkspace.cs"));

        Assert.Contains(
            "const multiplier = envelope.cpu_free_ratio * envelope.gpu_free_ratio *",
            processScores,
            StringComparison.Ordinal);
        Assert.Contains(
            "const budget = envelope.software_base_mean * multiplier;",
            processScores,
            StringComparison.Ordinal);
        Assert.Contains(
            "const score = raw_score + welfare_share;",
            processScores,
            StringComparison.Ordinal);

        foreach (var smartSource in new[] { smartPlanner, smartProtocol, managedSmartAbi })
        {
            Assert.DoesNotContain("welfare", smartSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("system_pressure", smartSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("free_ratio", smartSource, StringComparison.OrdinalIgnoreCase);
        }

        var formulaOwnerCount = Directory
            .EnumerateFiles(nativeSourceRoot, "*.zig", SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains(
                "const budget = envelope.software_base_mean * multiplier;",
                StringComparison.Ordinal));
        Assert.Equal(1, formulaOwnerCount);
        Assert.DoesNotContain(
            "var multiplier = envelope.CpuFreeRatio",
            managedWorkspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var budget = softwareBaseMean * multiplier",
            managedWorkspace,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SamplingPathHasNoManagedScoringRequestOrProcessProjection()
    {
        var appRoot = FindAppRoot();
        var sampling = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.Sampling.cs"));
        var models = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.Models.cs"));
        var classificationPath = Path.Combine(
            appRoot,
            "Domain",
            "Optimization",
            "HostManagerRuntimeClassification.cs");
        var classification = File.ReadAllText(classificationPath);

        Assert.DoesNotContain("OptimizationScoringRequest", sampling, StringComparison.Ordinal);
        Assert.DoesNotContain("ToProcessInput", sampling, StringComparison.Ordinal);
        Assert.DoesNotContain("ToProcessInput", models, StringComparison.Ordinal);
        Assert.Contains("HostManagerIdleCapacitySnapshot IdleCapacity", models, StringComparison.Ordinal);
        Assert.Contains("CreateIdleCapacity(hardware)", sampling, StringComparison.Ordinal);
        Assert.DoesNotContain("OptimizationScoring", classification, StringComparison.Ordinal);
        Assert.True(File.Exists(classificationPath));
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Domain",
            "Optimization",
            "Scoring",
            "OptimizationScoring.cs")));
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
                "Resource Manager",
                "Resource Manager-APP"));
            if (File.Exists(Path.Combine(sourceCandidate, "ResourceManager.App.csproj")))
            {
                return sourceCandidate;
            }
        }

        foreach (var seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(seed);
            var remainingParents = 32;
            while (directory is not null && remainingParents-- > 0)
            {
                var candidate = Path.Combine(directory.FullName, "Resource Manager", "Resource Manager-APP");
                if (File.Exists(Path.Combine(candidate, "ResourceManager.App.csproj")))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate Resource Manager-APP from the test output or working directory.");
    }
}
