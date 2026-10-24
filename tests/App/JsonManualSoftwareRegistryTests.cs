using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class JsonManualSoftwareRegistryTests : IDisposable
{
    [Fact]
    public async Task ChangingClassificationPreservesSourceAndStandaloneIdentities()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var classified = await registry.AddOrUpdateAsync(new("Installed", SoftwareKinds.Game, [], "windows-installed:one"), default);
        var reclassified = await registry.AddOrUpdateAsync(new("Installed", SoftwareKinds.Other, [], classified.Id), default);
        Assert.Equal("windows-installed:one", reclassified.Id);
        var standalone = await registry.AddOrUpdateAsync(new("Portable", SoftwareKinds.Game, [@"D:\Apps\Portable"], null), default);
        var changed = await registry.AddOrUpdateAsync(new("Portable", SoftwareKinds.Other, [@"D:\Apps\Portable"], null), default);
        Assert.Equal(standalone.Id, changed.Id);
        Assert.Equal(2, (await registry.GetAllAsync(default)).Count);
    }

    [Fact]
    public async Task AbsentFileIsNotReopenedAndCreatedFileAndWritesAreObserved()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var exceptions = 0;
        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException or DirectoryNotFoundException
                && args.Exception.Message.Contains(testRoot, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref exceptions);
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            var empty = await registry.GetAllAsync(default);
            for (var i = 0; i < 20; i++) Assert.Same(empty, await registry.GetAllAsync(default));
            Assert.Equal(0, exceptions);
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            var record = new ManualSoftwareRecord("manual:created", "Created", SoftwareKinds.Game, SoftwareKinds.Game,
                "manual", [], [], "", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(StoragePath, JsonSerializer.Serialize(new[] { record }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var loaded = await registry.GetAllAsync(default);
            Assert.Equal("Created", Assert.Single(loaded).Name);
            for (var i = 0; i < 20; i++) Assert.Same(loaded, await registry.GetAllAsync(default));
            Assert.True(await registry.RemoveAsync(record.Id, default));
            Assert.Empty(await registry.GetAllAsync(default));
            File.Delete(StoragePath);
            Assert.Empty(await registry.GetAllAsync(default));
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-manual-software-{Guid.NewGuid():N}");
    private string StoragePath => Path.Combine(testRoot, "Config", "manual-software.json");

    [Theory]
    [InlineData("Game", SoftwareKinds.Game)]
    [InlineData("highperformance", SoftwareKinds.HighPerformance)]
    [InlineData("High_Performance", SoftwareKinds.HighPerformance)]
    public async Task StoredLegacyKindLoadsAsCurrentKind(string storedKind, string expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var record = new ManualSoftwareRecord("manual:legacy", "Legacy", storedKind, storedKind,
            "manual", [], [], "", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(StoragePath, JsonSerializer.Serialize(new[] { record },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var loaded = Assert.Single(await registry.GetAllAsync(default));
        Assert.Equal(expected, loaded.Kind);
        Assert.Equal(expected, loaded.DisplayKind);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("游戏")]
    public async Task UnknownStoredKindFailsWithoutChangingTheFile(string storedKind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var record = new ManualSoftwareRecord("manual:legacy", "Legacy", storedKind, storedKind,
            "manual", [], [], "", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(new[] { record }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(StoragePath, json);
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.GetAllAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.AddOrUpdateAsync(
            new("New", SoftwareKinds.Game, [], null), default));
        Assert.Equal(json, await File.ReadAllTextAsync(StoragePath));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("游戏")]
    [InlineData("Game")]
    [InlineData("highperformance")]
    [InlineData("")]
    public async Task RequestsRejectUnknownOrLegacyKinds(string kind)
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddOrUpdateAsync(
            new("New", kind, [], "runtime:app"), default));
        Assert.False(File.Exists(StoragePath));
    }

    [Fact]
    public async Task RuntimeSourceCanBeClassifiedAsGeneralWithoutAKnownRoot()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var record = await registry.AddOrUpdateAsync(new("Runtime App", SoftwareKinds.Other, [], "runtime:app"), default);
        Assert.Equal("runtime:app", record.SourceSoftwareId);
        Assert.Equal(SoftwareKinds.Other, Assert.Single(await registry.GetAllAsync(default)).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddOrUpdateAsync(
            new("No source", SoftwareKinds.Other, [], null), default));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task CorruptRegistryReadRefusesReadsAndMutationsWithoutReplacingSource(string invalidJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, invalidJson);
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.GetAllAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.AddOrUpdateAsync(
            new("Game", SoftwareKinds.Game, [], null), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RemoveAsync("manual:game:missing", default));

        Assert.Equal(invalidJson, await File.ReadAllTextAsync(StoragePath));
    }

    [Fact]
    public async Task AddOrUpdateAtomicallyRetainsExistingManualRecords()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));

        var first = await registry.AddOrUpdateAsync(new("First Game", SoftwareKinds.Game, [], null), default);
        var second = await registry.AddOrUpdateAsync(new("High Performance", SoftwareKinds.HighPerformance, [], null), default);
        var loaded = await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default);
        var persisted = JsonSerializer.Deserialize<List<ManualSoftwareRecord>>(
            await File.ReadAllTextAsync(StoragePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, record => record.Id == first.Id);
        Assert.Contains(loaded, record => record.Id == second.Id);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StoragePath)!, "manual-software.json.*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
