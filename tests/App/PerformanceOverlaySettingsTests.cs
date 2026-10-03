using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Infrastructure.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlaySettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "resource-manager-overlay-settings-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DefaultsAndSaveAreNormalizedAndPersisted()
    {
        var store = CreateStore();
        var initial = await store.GetSoftwareAsync(" game ", CancellationToken.None);
        Assert.False(initial.Enabled);
        Assert.Equal("external", initial.Mode);
        Assert.Equal(14, initial.FontSizePx);
        Assert.Equal(PerformanceOverlayMetricIds.Defaults, initial.Metrics);

        var saved = await store.SaveSoftwareAsync(initial with
        {
            Enabled = true,
            Mode = "INJECTED",
            Metrics = ["target.FPS", "target.fps", "cpu.usage"],
            SizeMode = "windowratio",
            FontSizePx = 999,
            RegionWidthRatio = 0.001,
            RegionHeightRatio = double.NaN,
            Anchor = "bottomright",
            MarginPx = -5
        }, CancellationToken.None);
        Assert.Equal("injected", saved.Mode);
        Assert.Equal([PerformanceOverlayMetricIds.Fps, "cpu.usage"], saved.Metrics);
        Assert.Equal("windowRatio", saved.SizeMode);
        Assert.Equal(72, saved.FontSizePx);
        Assert.Equal(0.05, saved.RegionWidthRatio);
        Assert.Equal(0.2, saved.RegionHeightRatio);
        Assert.Equal("bottomRight", saved.Anchor);
        Assert.Equal(0, saved.MarginPx);

        var reloaded = await CreateStore().GetSoftwareAsync("GAME", CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(reloaded));
        Assert.True(reloaded.Enabled);
        var directory = Path.Combine(root, "UserData", "SoftwareProfiles");
        Assert.True(File.Exists(Path.Combine(directory, "performance-overlay.local.json")));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadNormalizesIncompleteAndDuplicateRecords()
    {
        var store = CreateStore();
        var directory = Directory.CreateDirectory(Path.Combine(root, "UserData", "SoftwareProfiles"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "performance-overlay.local.json"),
            """{"version":0,"software":[{"softwareId":"game","enabled":true,"mode":"invalid","metrics":null,"fontSizePx":1},{"softwareId":"GAME","enabled":false,"anchor":"topRight"},{"softwareId":" "}]}""");

        var document = await store.GetAsync(CancellationToken.None);
        var setting = Assert.Single(document.Software);
        Assert.Equal("GAME", setting.SoftwareId);
        Assert.False(setting.Enabled);
        Assert.Equal("external", setting.Mode);
        Assert.Equal("topRight", setting.Anchor);
        Assert.Equal(1, document.Version);
    }

    private JsonPerformanceOverlaySettingsStore CreateStore()
        => new(new OverlayHostEnvironment(Directory.CreateDirectory(Path.Combine(root, "APP")).FullName));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class OverlayHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
