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

    [Fact]
    public void CorruptExistingScoreFileCannotBeReadSavedOrResetAsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));
            var path = first.Save(new GpuPerformanceScoreOverrideRequest(
                [new GpuPerformanceScoreOverrideItem("gpu:0", 80)])).StoragePath;
            File.WriteAllText(path, "{invalid");
            var reopened = new JsonGpuPerformanceScoreOverrideStore(new TestHostEnvironment(root));

            Assert.Throws<JsonException>(() => reopened.LoadScores());
            Assert.Throws<JsonException>(() => reopened.Save(new GpuPerformanceScoreOverrideRequest([])));
            Assert.Throws<JsonException>(() => reopened.Reset());
            Assert.Equal("{invalid", File.ReadAllText(path));
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
