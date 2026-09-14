using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuCorePerformanceOverrideStoreTests
{
    [Fact]
    public void Save_NormalizesScoresAndLoadsByCpuName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-cpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonCpuCorePerformanceOverrideStore(new TestHostEnvironment(root));

            var result = store.Save(new CpuCorePerformanceOverrideRequest(
                "Synthetic CPU",
                [
                    new CpuCorePerformanceOverrideItem(0, 250),
                    new CpuCorePerformanceOverrideItem(1, 66.66),
                    new CpuCorePerformanceOverrideItem(-1, 100),
                    new CpuCorePerformanceOverrideItem(2, null)
                ]));

            var loaded = store.LoadScores("  Synthetic   CPU  ");

            Assert.EndsWith(Path.Combine("Config", "cpu-core-performance-overrides.json"), result.StoragePath);
            Assert.Equal(250, loaded[0]);
            Assert.Equal(66.66, loaded[1]);
            Assert.False(loaded.ContainsKey(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reset_RemovesOnlyRequestedCpuProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-cpu-overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonCpuCorePerformanceOverrideStore(new TestHostEnvironment(root));
            store.Save(new CpuCorePerformanceOverrideRequest(
                "CPU A",
                [new CpuCorePerformanceOverrideItem(0, 80)]));
            store.Save(new CpuCorePerformanceOverrideRequest(
                "CPU B",
                [new CpuCorePerformanceOverrideItem(0, 120)]));

            var result = store.Reset(" CPU A ");

            Assert.Empty(result.ScoresByCoreIndex);
            Assert.Empty(store.LoadScores("CPU A"));
            Assert.Equal(120, store.LoadScores("CPU B")[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VersionOneProfilesSurviveBaselineSaveAndReopen()
    {
        using var fixture = new StoreFixture();
        const string original = """
            {"version":1,"profiles":{
              "CPU A":{"cpuName":"CPU A","scoresByCoreIndex":{"0":825.5,"1":410},"updatedAt":"2026-01-01T00:00:00+00:00"},
              "CPU B":{"cpuName":"CPU B","scoresByCoreIndex":{"2":770},"updatedAt":"2026-02-01T00:00:00+00:00"}
            }}
            """;
        File.WriteAllText(fixture.Path, original);
        var store = fixture.Open();
        Assert.Null(store.LoadConfiguration("CPU A").BaselineRatio);
        Assert.Equal(original, File.ReadAllText(fixture.Path));

        store.SaveBaselineRatio(0.6);
        var reopened = fixture.Open();
        Assert.Equal(825.5, reopened.LoadScores("CPU A")[0]);
        Assert.Equal(410, reopened.LoadScores("CPU A")[1]);
        Assert.Equal(770, reopened.LoadScores("CPU B")[2]);
        Assert.Equal(0.6, reopened.LoadConfiguration("CPU A").BaselineRatio);
        Assert.Equal(0.6, reopened.LoadConfiguration("Unknown CPU").BaselineRatio);
        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(2, after.RootElement.GetProperty("version").GetInt32());
        foreach (var profile in before.RootElement.GetProperty("profiles").EnumerateObject())
        {
            Assert.Equal(profile.Value.GetProperty("updatedAt").GetDateTimeOffset(),
                after.RootElement.GetProperty("profiles").GetProperty(profile.Name).GetProperty("updatedAt").GetDateTimeOffset());
        }
    }

    [Fact]
    public void CoreAndBaselineSaveResetOnlyTheirOwnDimension()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Open();
        store.SaveBaselineRatio(0.5);
        store.Save(new CpuCorePerformanceOverrideRequest("CPU A", [new(0, 810)]));
        store.Save(new CpuCorePerformanceOverrideRequest("CPU B", [new(1, 490)]));
        Assert.Equal(0.5, store.LoadConfiguration("CPU A").BaselineRatio);
        store.Reset("CPU A");
        Assert.Equal(0.5, store.LoadConfiguration("CPU B").BaselineRatio);
        Assert.Equal(490, store.LoadScores("CPU B")[1]);
        store.ResetBaselineRatio();
        var reopened = fixture.Open();
        Assert.Null(reopened.LoadConfiguration("CPU B").BaselineRatio);
        Assert.Equal(490, reopened.LoadScores("CPU B")[1]);
        Assert.Empty(reopened.LoadScores("CPU A"));
    }

    [Fact]
    public async Task ConcurrentCoreAndBaselineWritesPreserveBothValues()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Open();
        using var start = new ManualResetEventSlim();
        var core = Task.Run(() => { start.Wait(); store.Save(new CpuCorePerformanceOverrideRequest("CPU", [new(0, 900)])); });
        var baseline = Task.Run(() => { start.Wait(); store.SaveBaselineRatio(0.75); });
        start.Set();
        await Task.WhenAll(core, baseline).WaitAsync(TimeSpan.FromSeconds(10));
        var actual = fixture.Open().LoadConfiguration("CPU");
        Assert.Equal(900, actual.ScoresByCoreIndex[0]);
        Assert.Equal(0.75, actual.BaselineRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e-310)]
    public void InvalidBaselineDoesNotTouchPersistedOrCachedValues(double ratio)
    {
        using var fixture = new StoreFixture();
        var store = fixture.Open();
        store.SaveBaselineRatio(0.8);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidOperationException>(() => store.SaveBaselineRatio(ratio));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(0.8, store.LoadConfiguration("CPU").BaselineRatio);
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!));
    }

    [Fact]
    public void ExistingReadersKeepCompleteOldDocumentAcrossAtomicReplacement()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Open();
        store.SaveBaselineRatio(0.7);
        using var reader = new StreamReader(new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        store.SaveBaselineRatio(0.6);
        using var old = JsonDocument.Parse(reader.ReadToEnd());
        using var current = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(0.7, old.RootElement.GetProperty("baselineRatio").GetDouble());
        Assert.Equal(0.6, current.RootElement.GetProperty("baselineRatio").GetDouble());
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("core")]
    [InlineData("reset-core")]
    [InlineData("reset-baseline")]
    public void RealCommitFailurePreservesOriginalFileAndCache(string operation)
    {
        using var fixture = new StoreFixture();
        var store = fixture.Open();
        store.Save(new CpuCorePerformanceOverrideRequest("CPU", [new(0, 820)]));
        store.SaveBaselineRatio(0.7);
        var before = File.ReadAllBytes(fixture.Path);
        File.SetAttributes(fixture.Path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<IOException>(() =>
            {
                switch (operation)
                {
                    case "baseline": store.SaveBaselineRatio(0.4); break;
                    case "core": store.Save(new CpuCorePerformanceOverrideRequest("CPU", [new(0, 100)])); break;
                    case "reset-core": store.Reset("CPU"); break;
                    case "reset-baseline": store.ResetBaselineRatio(); break;
                }
            });
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
            Assert.Equal(0.7, store.LoadConfiguration("CPU").BaselineRatio);
            Assert.Equal(820, store.LoadScores("CPU")[0]);
            Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.tmp"));
        }
        finally { File.SetAttributes(fixture.Path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("{\"version\":3,\"profiles\":{}}")]
    [InlineData("{\"version\":2,\"profiles\":{},\"baselineRatio\":0}")]
    [InlineData("{\"version\":2,\"profiles\":null}")]
    public void InvalidStoredDocumentIsNotReplacedByDefaults(string json)
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Path, json);
        var store = fixture.Open();
        Assert.Throws<InvalidDataException>(() => store.LoadConfiguration("CPU"));
        Assert.Throws<InvalidDataException>(() => store.SaveBaselineRatio(0.5));
        Assert.Equal(json, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void MalformedJsonCannotEraseTheExistingConfiguration()
    {
        using var fixture = new StoreFixture();
        const string malformed = "{\"version\":2,\"profiles\":";
        File.WriteAllText(fixture.Path, malformed);
        var store = fixture.Open();
        Assert.Throws<JsonException>(() => store.SaveBaselineRatio(0.5));
        Assert.Equal(malformed, File.ReadAllText(fixture.Path));
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rm-cpu-baseline-{Guid.NewGuid():N}");

        public StoreFixture() => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        public string Path => System.IO.Path.Combine(root, "Config", "cpu-core-performance-overrides.json");

        public JsonCpuCorePerformanceOverrideStore Open() => new(new TestHostEnvironment(root));

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ResourceManager.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
