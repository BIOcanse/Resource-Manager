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
        Assert.Equal(PerformanceOverlayMetricIds.Defaults, initial.Metrics);

        var saved = await store.SaveSoftwareAsync(initial with
        {
            Enabled = true,
            Mode = "INJECTED",
            Metrics = ["target.FPS", "target.fps", "cpu.usage"]
        }, CancellationToken.None);
        Assert.Equal("injected", saved.Mode);
        Assert.Equal([PerformanceOverlayMetricIds.Fps, "cpu.usage"], saved.Metrics);

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
        Assert.Equal(2, document.Version);
    }

    [Fact]
    public async Task AbsentFileIsNotReopenedAndCreationAndChangesAreObserved()
    {
        var store = CreateStore();
        var exceptions = 0;
        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException or DirectoryNotFoundException
                && args.Exception.Message.Contains(root, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref exceptions);
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            var empty = await store.GetAsync(default);
            for (var i = 0; i < 20; i++) Assert.Same(empty, await store.GetAsync(default));
            Assert.Equal(0, exceptions);
            var path = Path.Combine(root, "UserData", "SoftwareProfiles", "performance-overlay.local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, """{"version":1,"software":[{"softwareId":"game","enabled":true}]}""");
            var loaded = await store.GetAsync(default);
            Assert.True(Assert.Single(loaded.Software).Enabled);
            for (var i = 0; i < 20; i++) Assert.Same(loaded, await store.GetAsync(default));
            await store.SaveSoftwareAsync(Assert.Single(loaded.Software) with { Enabled = false }, default);
            Assert.False(Assert.Single((await store.GetAsync(default)).Software).Enabled);
            File.Delete(path);
            Assert.Empty((await store.GetAsync(default)).Software);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
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
