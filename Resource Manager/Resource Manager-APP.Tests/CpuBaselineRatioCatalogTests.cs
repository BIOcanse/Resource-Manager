using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuBaselineRatioCatalogTests
{
    private static readonly CpuBaselineRatioCatalog Catalog = CpuBaselineRatioCatalog.LoadEmbedded();

    [Fact]
    public void EveryEmbeddedIntelModelResolvesWithItsOwnSuffixAndCoreCount()
    {
        using var stream = typeof(CpuBaselineRatioCatalog).Assembly.GetManifestResourceStream(
            "ResourceManager.Configuration.Cpu.baseline-defaults.json")!;
        using var data = JsonDocument.Parse(stream);
        var models = data.RootElement.GetProperty("intelModels").EnumerateArray().ToArray();
        Assert.Equal(1167, models.Length);
        foreach (var model in models)
        {
            var name = model.GetProperty("name").GetString()!;
            var cores = model.GetProperty("physicalCores").GetInt32();
            var expected = model.GetProperty("ratio").GetDouble();
            foreach (var observed in new[] { name, $"Intel(R) {name} CPU @ 3.00GHz" })
            {
                var settings = Catalog.Resolve(CreateTopology(observed, "Intel", [cores]), null);
                Assert.Equal(name, settings.MatchedModel);
                Assert.Equal(expected, settings.Ratio);
                Assert.Equal(expected, settings.DefaultRatio);
                Assert.Null(settings.OverrideRatio);
                Assert.Equal(1 / expected, settings.Multiplier);
                Assert.Equal("2026-09-02", settings.DatasetVersion);
            }
        }
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 24, "Core i9-13900K", 0.5557)]
    [InlineData("Intel(R) Core(TM) i9-14900K", 24, "Core i9-14900K", 0.5557)]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", 24, "Core Ultra 9 285K", 0.5557)]
    [InlineData("Intel(R) Core(TM) i5-12600", 6, "Core i5-12600", 1)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12600K", 10, "Core i5-12600K", 0.7804)]
    public void WindowsBrandStringsRetainExactModelIdentity(string name, int cores, string model, double expected)
    {
        var settings = Catalog.Resolve(CreateTopology(name, "Intel", [cores]), null);
        Assert.Equal(model, settings.MatchedModel);
        Assert.Equal(expected, settings.Ratio);
    }

    [Theory]
    [InlineData("Intel Core i9-14900XYZ", 24, "Intel")]
    [InlineData("Intel Core i9-14900K", 16, "Intel")]
    [InlineData("Intel Core i9-14900K", 24, "Other")]
    public void UnknownSuffixDisabledCoresOrDifferentVendorDoNotGuessAReference(string name, int cores, string vendor)
    {
        var settings = Catalog.Resolve(CreateTopology(name, vendor, [cores]), null);
        Assert.Equal(1, settings.Ratio);
        Assert.Equal("uncompensated", settings.DefaultSource);
        Assert.Null(settings.MatchedModel);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D", 1, 8, 1)]
    [InlineData("AMD Ryzen 9 9955HX", 2, 8, 0.5)]
    public void HomogeneousAmdUsesOneReferencePerProvenCcd(string name, int ccds, int coresPerCcd, double expected)
    {
        var topology = CreateTopology(name, "AMD", Enumerable.Repeat(coresPerCcd, ccds).ToArray());
        var settings = Catalog.Resolve(topology, null);
        Assert.Equal(expected, settings.Ratio);
        Assert.StartsWith("amd:", settings.DefaultSource);
    }

    [Theory]
    [InlineData("CpuSetLastLevelCacheIndex")]
    [InlineData("L3CacheGroup")]
    [InlineData("Amd16CoreFallback")]
    [InlineData("fixture")]
    public void CacheGroupsOrFallbacksAreNotProofOfTwoCcds(string source)
    {
        var topology = CreateTopology("AMD Ryzen 7 3700X", "AMD", [4, 4], source);
        Assert.Equal("uncompensated", Catalog.Resolve(topology, null).DefaultSource);
    }

    [Fact]
    public void AsymmetricDisabledOrDuplicateAmdCoresAreNotAcceptedAsSymmetricCcds()
    {
        foreach (var layout in new[] { new[] { 6, 6 }, new[] { 7, 9 }, new[] { 4, 4, 4, 4 } })
        {
            var topology = CreateTopology("AMD Ryzen 9 9955HX", "AMD", layout);
            Assert.Equal("uncompensated", Catalog.Resolve(topology, null).DefaultSource);
        }
        var complete = CreateTopology("AMD Ryzen 9 9955HX", "AMD", [8, 8]);
        var duplicate = complete with { Ccds = [complete.Ccds[0], complete.Ccds[1] with { PhysicalCoreIndexes = complete.Ccds[0].PhysicalCoreIndexes }] };
        Assert.Equal("uncompensated", Catalog.Resolve(duplicate, null).DefaultSource);
    }

    [Fact]
    public void ManualOverrideIsIndependentOfDefaultMatchAndKeepsEqualValueIntent()
    {
        var unknown = Catalog.Resolve(CreateTopology("Unknown CPU", "Other", [4]), 0.6);
        Assert.Equal(0.6, unknown.Ratio);
        Assert.Equal(1, unknown.DefaultRatio);
        var same = Catalog.Resolve(CreateTopology("Core i5-12600", "Intel", [6]), 1);
        Assert.Equal(1, same.OverrideRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e-310)]
    public void InvalidRatioCannotEnterCompiledSettings(double ratio)
        => Assert.Throws<InvalidOperationException>(() => Catalog.Resolve(CreateTopology("CPU", "Other", [1]), ratio));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2}")]
    public void InvalidCatalogDoesNotSilentlyBecomeAnEmptyDefaultTable(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => CpuBaselineRatioCatalog.Read(stream));
    }

    [Fact]
    public void UnknownCatalogFieldIsRejectedByTheJsonBoundary()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"unknown\":1}"));
        Assert.Throws<JsonException>(() => CpuBaselineRatioCatalog.Read(stream));
    }

    internal static CpuTopologySnapshot CreateTopology(string name, string vendor, int[] coresPerCcd,
        string source = "RelationProcessorDie")
    {
        var cores = new List<CpuPhysicalCoreModel>();
        var logical = new List<CpuLogicalProcessorModel>();
        var ccds = new List<CpuCcdModel>();
        for (var ccdIndex = 0; ccdIndex < coresPerCcd.Length; ccdIndex++)
        {
            var coreIndexes = Enumerable.Range(cores.Count, coresPerCcd[ccdIndex]).ToArray();
            var ccdId = $"ccd:{ccdIndex}";
            foreach (var index in coreIndexes)
            {
                cores.Add(new CpuPhysicalCoreModel($"core:{index}", index, $"Core {index}", ccdId, 0, 100, null, [index], []));
                logical.Add(new CpuLogicalProcessorModel(index, index / 64, index % 64, $"core:{index}", ccdId, 100, null, true));
            }
            ccds.Add(new CpuCcdModel(ccdId, ccdIndex, $"CCD {ccdIndex}", null, coreIndexes, coreIndexes, source));
        }
        return new CpuTopologySnapshot(DateTimeOffset.UnixEpoch, name,
            new CpuSpecificationModel(name, vendor, "fixture", cores.Count, logical.Count, null, null, null, null, "fixture"),
            "fixture", "fixture", CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.Grid, "fixture", cores.Count, logical.Count, ccds.Count, false,
            ccds, cores, logical, []);
    }
}
