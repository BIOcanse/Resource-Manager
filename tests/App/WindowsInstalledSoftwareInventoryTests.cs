using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsInstalledSoftwareInventoryTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"resource-manager-installed-inventory-{Guid.NewGuid():N}");

    [Fact]
    public void HasValidRegisteredLocation_AcceptsExistingDirectory()
    {
        var installedRoot = Directory.CreateDirectory(Path.Combine(testRoot, "InstalledApp")).FullName;

        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([installedRoot]));
    }

    [Fact]
    public void HasValidRegisteredLocation_AcceptsExistingFile()
    {
        Directory.CreateDirectory(testRoot);
        var executablePath = Path.Combine(testRoot, "app.exe");
        File.WriteAllBytes(executablePath, []);

        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([executablePath]));
    }

    [Fact]
    public void HasValidRegisteredLocation_RejectsOnlyMissingLocations()
    {
        var missingRoot = Path.Combine(testRoot, "UninstalledApp");

        Assert.False(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([missingRoot]));
    }

    [Fact]
    public void HasValidRegisteredLocation_AcceptsEntryWithoutLocationEvidence()
    {
        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([]));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
