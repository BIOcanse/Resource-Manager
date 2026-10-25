using System.Text.Json;
using ResourceManager.Frontend.Packaging;

namespace Resource_Manager_APP.Tests;

public sealed class LegacyStartupModuleMigrationTests
{
    [Theory]
    [InlineData(true, "injected", 2)]
    [InlineData(false, "injected", 0)]
    [InlineData(true, "external", 0)]
    public void OfflineMigrationPreservesRawAndConvertsOnlyInjectedOperationalRequest(bool enabled, string mode, int moduleCount)
    {
        using var files = new StartupModuleTestCatalog();
        files.WriteFrontendModules();
        var directory = Directory.CreateDirectory(Path.Combine(files.Root, "UserData", "SoftwareProfiles")).FullName;
        var legacy = Path.Combine(directory, "performance-overlay.local.json");
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, software = new[] { new { softwareId = "game", enabled, mode } } });
        File.WriteAllBytes(legacy, raw);
        LegacyStartupModuleMigration.Migrate(files.Root, files.Root);
        var destination = Path.Combine(directory, "startup-modules.local.json");
        using var migrated = JsonDocument.Parse(File.ReadAllBytes(destination));
        Assert.Equal(1, migrated.RootElement.GetProperty("version").GetInt32());
        var selection = Assert.Single(migrated.RootElement.GetProperty("software").EnumerateArray());
        Assert.Equal("game", selection.GetProperty("softwareId").GetString());
        Assert.Equal(moduleCount, selection.GetProperty("moduleIds").GetArrayLength());
        Assert.Equal(raw, File.ReadAllBytes(legacy));
        var image = File.ReadAllBytes(destination);
        LegacyStartupModuleMigration.Migrate(files.Root, files.Root);
        Assert.Equal(image, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ExistingExplicitEmptySelectionWinsOverOldEnabledRequest()
    {
        using var files = new StartupModuleTestCatalog();
        var directory = Directory.CreateDirectory(Path.Combine(files.Root, "UserData", "SoftwareProfiles")).FullName;
        File.WriteAllText(Path.Combine(directory, "performance-overlay.local.json"), """{"software":[{"softwareId":"game","enabled":true,"mode":"injected"}]}""");
        var destination = Path.Combine(directory, "startup-modules.local.json");
        var empty = """{"version":1,"software":[]}""";
        File.WriteAllText(destination, empty);
        LegacyStartupModuleMigration.Migrate(files.Root, files.Root);
        Assert.Equal(empty, File.ReadAllText(destination));
    }

    [Fact]
    public void MissingAdmittedRendererCannotSilentlyDiscardAnExistingInjectedRequest()
    {
        using var files = new StartupModuleTestCatalog();
        var directory = Directory.CreateDirectory(Path.Combine(files.Root, "UserData", "SoftwareProfiles")).FullName;
        File.WriteAllText(Path.Combine(directory, "performance-overlay.local.json"), """{"software":[{"softwareId":"game","enabled":true,"mode":"injected"}]}""");
        Assert.Throws<InvalidDataException>(() => LegacyStartupModuleMigration.Migrate(files.Root, files.Root));
        Assert.False(File.Exists(Path.Combine(directory, "startup-modules.local.json")));
    }
}
