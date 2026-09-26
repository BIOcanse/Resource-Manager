namespace Resource_Manager_APP.Tests;

public sealed class HostManagerControlZoneNameGuardTests
{
    [Fact]
    public void HostManagerControlZonesHaveNoLegacySmartTypeOrFileNames()
    {
        var appRoot = FindAppRoot();
        var zoneRoot = Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "SmartControl",
            "Zones");
        var suffixes = new[]
        {
            "Coordinator",
            "Sampling",
            "Scoring",
            "PolicyExecution",
            "HardwarePlacement"
        };

        foreach (var suffix in suffixes)
        {
            var legacyPath = Path.Combine(zoneRoot, $"Smart{suffix}ControlZone.cs");
            var hostManagerPath = Path.Combine(zoneRoot, $"HostManager{suffix}ControlZone.cs");
            Assert.False(File.Exists(legacyPath), legacyPath);
            Assert.True(File.Exists(hostManagerPath), hostManagerPath);
            var source = File.ReadAllText(hostManagerPath);
            Assert.Contains($"class HostManager{suffix}ControlZone", source, StringComparison.Ordinal);
            Assert.DoesNotContain($"class Smart{suffix}ControlZone", source, StringComparison.Ordinal);
        }

        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "OptimizationServiceRegistration.cs"));
        var previousIndex = -1;
        foreach (var suffix in suffixes)
        {
            var expected = $"AddHostManagerSmartControlZone<HostManager{suffix}ControlZone>()";
            var currentIndex = registration.IndexOf(expected, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, expected);
            Assert.DoesNotContain(
                $"AddHostManagerSmartControlZone<Smart{suffix}ControlZone>()",
                registration,
                StringComparison.Ordinal);
            previousIndex = currentIndex;
        }
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", "src", "Core"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException("Could not locate Resource Manager-APP from the test source path.");
    }
}
