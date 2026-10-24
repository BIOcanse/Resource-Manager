using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class JsonManualSoftwareRegistryTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-manual-software-{Guid.NewGuid():N}");
    private string StoragePath => Path.Combine(testRoot, "Config", "manual-software.json");

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
