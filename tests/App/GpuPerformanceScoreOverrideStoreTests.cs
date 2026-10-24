using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPerformanceScoreOverrideStoreTests
{
    [Fact]
    public void Save_NormalizesScoresAndLoadsByGpuId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));

            var result = store.Save(new GpuPerformanceScoreOverrideRequest(
            [
                new GpuPerformanceScoreOverrideItem("gpu:1", 250),
                new GpuPerformanceScoreOverrideItem("gpu:0", 66.66),
                new GpuPerformanceScoreOverrideItem("", 100),
                new GpuPerformanceScoreOverrideItem("gpu:2", null)
            ]));

            var loaded = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root)).LoadScores();

            Assert.EndsWith(Path.Combine("Config", "gpu-performance-score-overrides.json"), result.StoragePath);
            Assert.Equal(66.66, loaded["gpu:0"]);
            Assert.Equal(250, loaded["gpu:1"]);
            Assert.False(loaded.ContainsKey("gpu:2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reset_RemovesAllGpuScoreOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));
            store.Save(new GpuPerformanceScoreOverrideRequest(
            [
                new GpuPerformanceScoreOverrideItem("gpu:0", 80),
                new GpuPerformanceScoreOverrideItem("gpu:1", 120)
            ]));

            var result = store.Reset();

            Assert.Empty(result.ScoresByGpuId);
            Assert.Empty(new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root)).LoadScores());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"scoresByGpuId\":null}")]
    public void CorruptExistingScoreFileIsUnavailableButCanBeReplacedOrReset(string corrupt)
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));
            var path = first.Save(new GpuPerformanceScoreOverrideRequest(
                [new GpuPerformanceScoreOverrideItem("gpu:0", 80)])).StoragePath;
            File.WriteAllText(path, corrupt);
            var reopened = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));

            Assert.True(Record.Exception(() => reopened.LoadScores()) is JsonException or InvalidDataException);
            Assert.Equal(corrupt, File.ReadAllText(path));
            var scores = ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimePlanCompiler.CompileHardwareScores(
                ["general"], "cpu", new Dictionary<int, double> { [0] = 42 }, reopened);
            Assert.NotNull(scores.GpuPerformanceScoreUnavailableReason);
            Assert.Equal(42, scores.ResolveCpuPerformanceScore(0, 10));
            Assert.Throws<InvalidOperationException>(() => scores.ResolveGpuPerformanceScore(null!));
            reopened.Save(new GpuPerformanceScoreOverrideRequest([new("gpu:0", 90)]));
            Assert.Equal(90, reopened.LoadScores()["gpu:0"]);
            File.WriteAllText(path, corrupt);
            Assert.True(Record.Exception(() => reopened.LoadScores()) is JsonException or InvalidDataException);
            Assert.Empty(reopened.Reset().ScoresByGpuId);
            Assert.Empty(reopened.LoadScores());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ResourceManager.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
