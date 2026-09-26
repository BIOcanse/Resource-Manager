using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuApiFirstHistoryCommitTests
{
    [Theory]
    [InlineData(GpuGraphicsApi.D3D9)]
    [InlineData(GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.OpenGL)]
    [InlineData(GpuGraphicsApi.Vulkan)]
    public async Task FirstCommitCreatesTheOriginalDocumentWithoutPriorMetadata(GpuGraphicsApi api)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var history = await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), api, default);
        var process = Assert.Single(history.Processes);
        Assert.Equal(JsonGpuPlacementProcessHistoryStore.BuildProcessKey("target", files.Target), process.ProcessKey);
        Assert.Equal(api, process.GraphicsApi);
        Assert.Equal(files.Target, process.ExecutablePath);
        Assert.Contains("actual-api-call", process.EvidenceSources);
        var document = JsonSerializer.Deserialize<GpuPlacementProcessHistoryDocument>(
            await File.ReadAllTextAsync(files.HistoryPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(GpuPlacementPolicyDocumentVersions.Current, document.Version);
        Assert.Equal(api, Assert.Single(Assert.Single(document.SoftwareHistories).Processes).GraphicsApi);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(files.HistoryPath)!, "*.tmp"));
    }

    [Fact]
    public async Task FirstCommitPreservesExistingProcessFactsAndLaterMetadataCannotReplaceIt()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var before = Assert.Single((await store.ObserveAsync(files.Request(), default)).Processes);
        var saved = Assert.Single((await store.SaveFirstGraphicsApiAsync(
            "software", "Renamed", files.Instance(43), GpuGraphicsApi.Vulkan, default)).Processes);
        Assert.Equal(before.FirstObservedAt, saved.FirstObservedAt);
        Assert.Equal(before.LastObservedAt, saved.LastObservedAt);
        Assert.Equal(before.LastProcessId, saved.LastProcessId);
        Assert.Equal(before.Architecture, saved.Architecture);
        Assert.Equal(before.Confidence, saved.Confidence);
        Assert.Equal(before.ObservationCount, saved.ObservationCount);
        Assert.Contains("fixture", saved.EvidenceSources);
        await store.ObserveAsync(files.Request(44) with
        {
            Processes = [files.Input(44) with { Architecture = "arm64", EvidenceSources = ["new-metadata"] }]
        }, default);
        var after = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(GpuGraphicsApi.Vulkan, after.GraphicsApi);
        Assert.Equal("arm64", after.Architecture);
        Assert.Contains("actual-api-call", after.EvidenceSources);
        Assert.Contains("new-metadata", after.EvidenceSources);
    }

    [Fact]
    public async Task CompetingMetadataAndApiCommitsKeepAllSoftwareAndPathRecords()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var operations = Enumerable.Range(0, 16).SelectMany(i =>
        {
            var id = $"software-{i % 4}";
            var instance = files.Instance(42 + i) with { ExecutablePath = Path.Combine(files.Root, $"target-{i}.exe") };
            return new[]
            {
                store.ObserveAsync(new(id, id, [new(instance.ProcessName, instance.ExecutablePath, "x64", instance.ProcessId, ["metadata"])]), default),
                store.SaveFirstGraphicsApiAsync(id, id, instance, GpuGraphicsApi.D3D12, default)
            };
        });
        await Task.WhenAll(operations);
        for (var i = 0; i < 4; i++)
        {
            var history = await new JsonGpuPlacementProcessHistoryStore(files).GetSoftwareHistoryAsync($"software-{i}", "", default);
            Assert.Equal(4, history.Processes.Count);
            Assert.All(history.Processes, process =>
            {
                Assert.Equal(GpuGraphicsApi.D3D12, process.GraphicsApi);
                Assert.Equal("x64", process.Architecture);
                Assert.Contains("metadata", process.EvidenceSources);
                Assert.Contains("actual-api-call", process.EvidenceSources);
            });
        }
    }

    [Fact]
    public async Task CaseAndCanonicalPathReuseTheCommittedWinnerWithoutRewriting()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.D3D9, default);
        var bytes = await File.ReadAllBytesAsync(files.HistoryPath);
        var result = await store.SaveFirstGraphicsApiAsync("SOFTWARE", "Changed", files.Instance(100) with
        {
            ExecutablePath = Path.Combine(files.Root, ".", "TARGET.EXE")
        }, GpuGraphicsApi.D3D12, default);
        Assert.Equal(GpuGraphicsApi.D3D9, Assert.Single(result.Processes).GraphicsApi);
        Assert.Equal("Software", result.SoftwareName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(files.HistoryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(9)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task UnidentifiedAndAmbiguousResultsNeverCreateHistory(int api)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveFirstGraphicsApiAsync(
            "software", "Software", files.Instance(), (GpuGraphicsApi)api, default));
        Assert.False(File.Exists(files.HistoryPath));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("path-empty")]
    [InlineData("path-relative")]
    [InlineData("path-root")]
    [InlineData("null-process")]
    public async Task InvalidInstanceStructureNeverCreatesHistory(string invalid)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var instance = invalid switch
        {
            "pid" => files.Instance(4),
            "birth" => files.Instance() with { ProcessStartKey = 0 },
            "path-empty" => files.Instance() with { ExecutablePath = "" },
            "path-relative" => files.Instance() with { ExecutablePath = "target.exe" },
            "path-root" => files.Instance() with { ExecutablePath = Path.GetPathRoot(files.Root)! },
            _ => null!
        };
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveFirstGraphicsApiAsync(
            "software", "Software", instance, GpuGraphicsApi.D3D11, default));
        Assert.False(File.Exists(files.HistoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledCommitDoesNotReturnOrPublishACandidate(bool hasMetadata)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        if (hasMetadata) await store.ObserveAsync(files.Request(), default);
        var before = File.Exists(files.HistoryPath) ? File.ReadAllBytes(files.HistoryPath) : null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveFirstGraphicsApiAsync(
            "software", "Software", files.Instance(), GpuGraphicsApi.OpenGL, cancellation.Token));
        Assert.Equal(before, File.Exists(files.HistoryPath) ? File.ReadAllBytes(files.HistoryPath) : null);
        var retry = await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.Vulkan, default);
        Assert.Equal(GpuGraphicsApi.Vulkan, Assert.Single(retry.Processes).GraphicsApi);
    }

    [Fact]
    public async Task NativeCommitFailurePreservesOldFileAndPreparedEvidenceWithoutCachingCandidate()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await store.ObserveAsync(files.Request(), default);
        var before = File.ReadAllBytes(files.HistoryPath);
        var attributes = File.GetAttributes(files.HistoryPath);
        File.SetAttributes(files.HistoryPath, attributes | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveFirstGraphicsApiAsync(
                "software", "Software", files.Instance(), GpuGraphicsApi.OpenGL, default));
            Assert.Equal(before, File.ReadAllBytes(files.HistoryPath));
            var pending = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(files.HistoryPath)!, "*.tmp"));
            Assert.Contains("\"OpenGL\"", File.ReadAllText(pending), StringComparison.Ordinal);
            Assert.Null(Assert.Single((await store.GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
        }
        finally
        {
            File.SetAttributes(files.HistoryPath, attributes);
        }
        var committed = await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.Vulkan, default);
        Assert.Equal(GpuGraphicsApi.Vulkan, Assert.Single(committed.Processes).GraphicsApi);
        Assert.Equal(GpuGraphicsApi.Vulkan, Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
    }
}
