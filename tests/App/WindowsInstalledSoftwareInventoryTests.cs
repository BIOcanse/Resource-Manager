using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite.Information;
using ResourceManager.App.Domain.Software;
using Microsoft.Win32;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsInstalledSoftwareInventoryTests : IDisposable
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreadableEntryDoesNotPreventLaterEntries(bool denied)
    {
        var visited = new List<string>();
        foreach (var name in new[] { "first", "bad", "last" })
            WindowsInstalledSoftwareInventory.ReadInventoryEntry(name, entry =>
            {
                if (entry == "bad")
                {
                    if (denied) throw new UnauthorizedAccessException();
                    throw new IOException();
                }
                visited.Add(entry);
            });
        Assert.Equal(new[] { "first", "last" }, visited);
        Assert.Throws<OperationCanceledException>(() => WindowsInstalledSoftwareInventory.ReadInventoryEntry(
            "cancel", _ => throw new OperationCanceledException()));
    }

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

    [Fact]
    public void InventoryRootsIncludeOnlyTheSelectedUserSidAndBothRegistryViews()
    {
        const string userSid = "S-1-5-21-1-2-3-1001";
        var roots = WindowsInstalledSoftwareInventory.CreateInventoryRoots(
            [userSid, userSid, "S-1-5-18"]);

        Assert.Equal(4, roots.Count);
        Assert.Equal(
            new[] { RegistryHive.LocalMachine, RegistryHive.LocalMachine, RegistryHive.Users, RegistryHive.Users },
            roots.Select(static root => root.Hive).ToArray());
        Assert.Equal(
            new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Registry64, RegistryView.Registry32 },
            roots.Select(static root => root.View).ToArray());
        Assert.Equal(new string?[] { null, null, userSid, userSid },
            roots.Select(static root => root.UserSid).ToArray());
    }

    [Fact]
    public async Task CachedInventoryIsBoundToOneCallerSidAtATime()
    {
        var reads = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ =>
        {
            reads++;
            return [new InstalledSoftwareEntry($"entry-{reads}", $"Entry {reads}", null, null, null, [], null, "recorded")];
        });

        var first = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);
        var second = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1002", default);
        var firstAgain = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);

        Assert.Equal("Entry 1", Assert.Single(first).Name);
        Assert.Equal("Entry 2", Assert.Single(second).Name);
        Assert.Equal("Entry 3", Assert.Single(firstAgain).Name);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task CallerScopedInventoryDoesNotLeakIntoUnscopedInformationFields()
    {
        var inventory = new WindowsInstalledSoftwareInventory(_ => [
            new InstalledSoftwareEntry("user-entry", "User Entry", null, null, null, [], null, "HKU")]);

        _ = await inventory.GetInstalledSoftwareAsync(default);
        Assert.NotEmpty(inventory.ReadInformation().Devices);

        _ = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);

        Assert.Empty(inventory.ReadInformation().Devices);
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
