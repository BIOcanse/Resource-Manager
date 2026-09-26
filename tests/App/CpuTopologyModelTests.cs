using ResourceManager.App.Domain.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyModelTests
{
    [Fact]
    public void AverageUsage_AggregatesOnlyAvailableLogicalProcessors()
    {
        var usage = CpuTopologyMath.AverageUsage([25, null, 75]);

        Assert.Equal(50, usage);
    }

    [Fact]
    public void AverageUsage_ReturnsNullWhenNoLogicalUsageIsAvailable()
    {
        var usage = CpuTopologyMath.AverageUsage([null, null]);

        Assert.Null(usage);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesMainstreamIntelDesktopHybrid()
    {
        var preset = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i7-14700K");

        Assert.Equal("Intel", preset.Vendor);
        Assert.True(preset.KnownHybrid);
        Assert.False(preset.KnownHomogeneous);
        Assert.Equal(8, preset.ExpectedPerformanceCoreCount);
        Assert.Equal(12, preset.ExpectedEfficientCoreCount);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesCoreUltraDesktopHybrid()
    {
        var preset = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) Ultra 9 285K");

        Assert.Equal("Intel", preset.Vendor);
        Assert.True(preset.KnownHybrid);
        Assert.Equal(8, preset.ExpectedPerformanceCoreCount);
        Assert.Equal(16, preset.ExpectedEfficientCoreCount);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesIntel12400AsHomogeneous()
    {
        var preset = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i5-12400F");

        Assert.Equal("Intel", preset.Vendor);
        Assert.False(preset.KnownHybrid);
        Assert.True(preset.KnownHomogeneous);
        Assert.False(preset.HasExpectedCoreLayout);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesIntel12600KButNot12600AsHybrid()
    {
        var unlocked = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i5-12600K");
        var locked = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i5-12600");

        Assert.True(unlocked.KnownHybrid);
        Assert.Equal(6, unlocked.ExpectedPerformanceCoreCount);
        Assert.Equal(4, unlocked.ExpectedEfficientCoreCount);
        Assert.False(locked.KnownHybrid);
        Assert.True(locked.KnownHomogeneous);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesCurrentDesktopCoreUltraPlusSkus()
    {
        var ultra7 = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) Ultra 7 270K Plus");
        var ultra5 = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) Ultra 5 250K Plus");

        Assert.Equal(8, ultra7.ExpectedPerformanceCoreCount);
        Assert.Equal(16, ultra7.ExpectedEfficientCoreCount);
        Assert.Equal(6, ultra5.ExpectedPerformanceCoreCount);
        Assert.Equal(12, ultra5.ExpectedEfficientCoreCount);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesIntel13700HAsMobileExactHybrid()
    {
        var preset = CpuCorePerformancePresetResolver.Classify("13th Gen Intel(R) Core(TM) i7-13700H");

        Assert.Equal("Intel", preset.Vendor);
        Assert.True(preset.KnownHybrid);
        Assert.True(preset.HasExpectedCoreLayout);
        Assert.Equal(6, preset.ExpectedPerformanceCoreCount);
        Assert.Equal(8, preset.ExpectedEfficientCoreCount);
        Assert.Equal(20, preset.ExpectedLogicalProcessorCount);
        Assert.Contains("mobile", preset.Family, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ClassifiesRepresentativeIntelMobileCoreSkus()
    {
        var alderLakeH = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i7-12700H");
        var alderLakeU = CpuCorePerformancePresetResolver.Classify("12th Gen Intel(R) Core(TM) i5-1235U");
        var raptorLakeHx = CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i9-13900HX");

        Assert.Equal(6, alderLakeH.ExpectedPerformanceCoreCount);
        Assert.Equal(8, alderLakeH.ExpectedEfficientCoreCount);
        Assert.Equal(20, alderLakeH.ExpectedLogicalProcessorCount);
        Assert.Equal(2, alderLakeU.ExpectedPerformanceCoreCount);
        Assert.Equal(8, alderLakeU.ExpectedEfficientCoreCount);
        Assert.Equal(12, alderLakeU.ExpectedLogicalProcessorCount);
        Assert.Equal(8, raptorLakeHx.ExpectedPerformanceCoreCount);
        Assert.Equal(16, raptorLakeHx.ExpectedEfficientCoreCount);
        Assert.Equal(32, raptorLakeHx.ExpectedLogicalProcessorCount);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesCoreUltraSeries1MobileWithLowPowerEfficientCores()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 7 155H",
            CreateCores(16, index => index < 6 ? 2 : 1));

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[6].PerformanceScore);
        Assert.Equal(45, result.ScoresByCoreIndex[14].PerformanceScore);
        Assert.Equal("LP-E-core", result.ScoresByCoreIndex[15].PerformanceClass);
        Assert.Contains(result.Notes, note => note.Contains("6P + 8E + 2LP-E", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesCoreUltraSeries2MobileWithoutSmt()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 9 285H",
            CreateCores(16, _ => 1));

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[6].PerformanceScore);
        Assert.Equal(45, result.ScoresByCoreIndex[14].PerformanceScore);
        Assert.Contains(result.Notes, note => note.Contains("6P + 8E + 2LP-E", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesCoreUltraVSeriesAsPerformanceAndLowPowerCores()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 7 268V",
            CreateCores(8, _ => 1));

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(100, result.ScoresByCoreIndex[3].PerformanceScore);
        Assert.Equal(45, result.ScoresByCoreIndex[4].PerformanceScore);
        Assert.Equal("LP-E-core", result.ScoresByCoreIndex[7].PerformanceClass);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesIntelCoreSeriesMobileSkus()
    {
        var core7U = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) 7 processor 150U",
            CreateCores(10, index => index < 2 ? 2 : 1));
        var core7H = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) 7 processor 240H",
            CreateCores(10, index => index < 6 ? 2 : 1));

        Assert.Equal(2, core7U.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "P-core"));
        Assert.Equal(8, core7U.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "E-core"));
        Assert.Equal(6, core7H.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "P-core"));
        Assert.Equal(4, core7H.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "E-core"));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesIntelCoreUltraHxMobileSkus()
    {
        var ultra9Hx = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 9 285HX",
            CreateCores(24, _ => 1));
        var ultra7Hx = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 7 265HX",
            CreateCores(20, _ => 1));
        var ultra5Hx = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 5 235HX",
            CreateCores(14, _ => 1));

        Assert.Equal(8, ultra9Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "P-core"));
        Assert.Equal(16, ultra9Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "E-core"));
        Assert.Equal(8, ultra7Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "P-core"));
        Assert.Equal(12, ultra7Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "E-core"));
        Assert.Equal(6, ultra5Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "P-core"));
        Assert.Equal(8, ultra5Hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "E-core"));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesIntelNSeriesAsLowPowerX64Mobile()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) i3-N305",
            CreateCores(8, _ => 1));

        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal(100, score.PerformanceScore));
        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal("Intel-N-core", score.PerformanceClass));
        Assert.Contains(result.Notes, note => note.Contains("8 Intel-N-core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_UsesCpuSetEfficiencyBeforeShapeFallback()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "Intel(R) Core(TM) Ultra 9 285H",
            [
                new CpuCorePerformanceInput(0, 0, 0, null, 2),
                new CpuCorePerformanceInput(1, 0, 2, null, 1)
            ]);

        Assert.True(result.ScoresByCoreIndex[1].PerformanceScore > result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal("windows-cpu-set-efficiency", result.ScoresByCoreIndex[0].Source);
        Assert.Contains(result.Notes, note => note.Contains("CPU Sets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_UsesIntelHybridDesktopPresetBeforeWindowsShapeFallback()
    {
        var cores = Enumerable.Range(0, 20)
            .Select(index => new CpuCorePerformanceInput(
                index,
                0,
                null,
                null,
                index < 8 ? 2 : 1))
            .ToArray();

        var result = CpuCorePerformancePresetResolver.Resolve("Intel(R) Core(TM) i7-14700K", cores);

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(100, result.ScoresByCoreIndex[7].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[8].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[19].PerformanceScore);
        Assert.Contains(result.Notes, note => note.Contains("8P + 12E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ScoresByCoreIndex.Values, score => score.Source.Contains("intel-core-14th-i7", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_SupportsCoreUltraDesktopWithoutSmt()
    {
        var cores = Enumerable.Range(0, 24)
            .Select(index => new CpuCorePerformanceInput(index, 0, null, null, 1))
            .ToArray();

        var result = CpuCorePerformancePresetResolver.Resolve("Intel(R) Core(TM) Ultra 9 285K", cores);

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(100, result.ScoresByCoreIndex[7].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[8].PerformanceScore);
        Assert.Equal(65, result.ScoresByCoreIndex[23].PerformanceScore);
        Assert.Contains(result.Notes, note => note.Contains("8P + 16E", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_FallsBackWhenPresetLayoutDoesNotMatchActualTopology()
    {
        var cores = Enumerable.Range(0, 16)
            .Select(index => new CpuCorePerformanceInput(index, 0, null, null, index < 8 ? 2 : 1))
            .ToArray();

        var result = CpuCorePerformancePresetResolver.Resolve("Intel(R) Core(TM) i7-14700K", cores);

        Assert.Contains(result.Notes, note => note.Contains("不一致", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Notes, note => note.Contains("8P + 8E", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("model-preset:intel-hybrid-thread-shape", result.ScoresByCoreIndex[0].Source);
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_KeepsAmdRyzenHomogeneousByDefault()
    {
        var cores = Enumerable.Range(0, 16)
            .Select(index => new CpuCorePerformanceInput(index, 0, null, null, 2))
            .ToArray();

        var result = CpuCorePerformancePresetResolver.Resolve("AMD Ryzen 9 8940HX with Radeon Graphics", cores);

        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal(100, score.PerformanceScore));
        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal("Zen-core", score.PerformanceClass));
        Assert.Contains(result.Notes, note => note.Contains("16 Zen-core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_DoesNotForceX3dIntoSingleDimensionalCoreRank()
    {
        var cores = Enumerable.Range(0, 16)
            .Select(index => new CpuCorePerformanceInput(index, 0, null, null, 2))
            .ToArray();

        var result = CpuCorePerformancePresetResolver.Resolve("AMD Ryzen 9 7950X3D 16-Core Processor", cores);

        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal(100, score.PerformanceScore));
        Assert.Contains(result.Notes, note => note.Contains("X3D", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesAmdRyzenAiZen5cAsHigherThanIntelECoreClass()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI 9 HX 370",
            CreateCores(12, _ => 2));

        Assert.Equal(100, result.ScoresByCoreIndex[0].PerformanceScore);
        Assert.Equal(82, result.ScoresByCoreIndex[4].PerformanceScore);
        Assert.Equal("Zen5c-core", result.ScoresByCoreIndex[11].PerformanceClass);
        Assert.Contains(result.Notes, note => note.Contains("4 Zen5-core + 8 Zen5c-core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesAmdRyzenAi300And400MobileVariants()
    {
        var ai365 = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI 9 H 365",
            CreateCores(10, _ => 2));
        var ai470 = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI 9 HX PRO 470",
            CreateCores(12, _ => 2));
        var ai340 = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI 5 340",
            CreateCores(6, _ => 2));

        Assert.Equal(4, ai365.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5-core"));
        Assert.Equal(6, ai365.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5c-core"));
        Assert.Equal(4, ai470.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5-core"));
        Assert.Equal(8, ai470.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5c-core"));
        Assert.Equal(3, ai340.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5-core"));
        Assert.Equal(3, ai340.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5c-core"));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesAmdRyzenMobileSuffixesAsHomogeneousZen()
    {
        var hx = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen 9 9955HX",
            CreateCores(16, _ => 2));
        var hs = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen 9 8945HS",
            CreateCores(8, _ => 2));
        var h = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen 7 7840H",
            CreateCores(8, _ => 2));
        var u = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen 5 PRO 7640U",
            CreateCores(6, _ => 2));

        Assert.Equal(16, hx.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen-core"));
        Assert.Equal(8, hs.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen-core"));
        Assert.Equal(8, h.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen-core"));
        Assert.Equal(6, u.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen-core"));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_TracksAmdMobileX3dAsCcdRoleNotCoreRank()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen 9 9955HX3D",
            CreateCores(16, _ => 2));

        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal(100, score.PerformanceScore));
        Assert.Contains(result.Notes, note => note.Contains("X3D", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_ResolvesAmdRyzenZHandheldProcessors()
    {
        var z2Extreme = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI Z2 Extreme",
            CreateCores(8, _ => 2));
        var z2Go = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen Z2 Go",
            CreateCores(4, _ => 2));

        Assert.Equal(3, z2Extreme.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5-core"));
        Assert.Equal(5, z2Extreme.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen5c-core"));
        Assert.Equal(4, z2Go.ScoresByCoreIndex.Values.Count(score => score.PerformanceClass == "Zen-core"));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_KeepsAmdRyzenAiMaxHomogeneous()
    {
        var result = CpuCorePerformancePresetResolver.Resolve(
            "AMD Ryzen AI Max+ 395",
            CreateCores(16, _ => 2));

        Assert.All(result.ScoresByCoreIndex.Values, score => Assert.Equal(100, score.PerformanceScore));
        Assert.Contains(result.Notes, note => note.Contains("amd-ryzen-ai-max", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Notes, note => note.Contains("同构", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCorePerformancePresetResolver_DoesNotClassifyArmSocAsWindowsX64CpuPreset()
    {
        var preset = CpuCorePerformancePresetResolver.Classify("Qualcomm Snapdragon X Elite - X1E-80-100");

        Assert.Equal("Unknown", preset.Vendor);
        Assert.False(preset.KnownHybrid);
        Assert.False(preset.KnownHomogeneous);
    }

    [Fact]
    public void CpuTopologyVisualLayoutResolver_UsesRingBusForIntel()
    {
        var desktop = CpuTopologyVisualLayoutResolver.Resolve(
            CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) i7-14700K"),
            ccdCount: 1);
        var mobile = CpuTopologyVisualLayoutResolver.Resolve(
            CpuCorePerformancePresetResolver.Classify("Intel(R) Core(TM) Ultra 9 285HX"),
            ccdCount: 1);

        Assert.Equal(CpuTopologyVisualLayoutKinds.RingBus, desktop.Kind);
        Assert.Equal(CpuTopologyVisualLayoutKinds.RingBus, mobile.Kind);
    }

    [Fact]
    public void CpuTopologyVisualLayoutResolver_UsesCcdGridForAmdMobileAndApu()
    {
        var hx = CpuTopologyVisualLayoutResolver.Resolve(
            CpuCorePerformancePresetResolver.Classify("AMD Ryzen 9 8940HX with Radeon Graphics"),
            ccdCount: 2);
        var u = CpuTopologyVisualLayoutResolver.Resolve(
            CpuCorePerformancePresetResolver.Classify("AMD Ryzen 7 PRO 7840U"),
            ccdCount: 1);

        Assert.Equal(CpuTopologyVisualLayoutKinds.CcdGrid, hx.Kind);
        Assert.Equal(CpuTopologyVisualLayoutKinds.CcdGrid, u.Kind);
    }

    private static CpuCorePerformanceInput[] CreateCores(
        int count,
        Func<int, int> logicalProcessorCountByIndex)
    {
        return Enumerable.Range(0, count)
            .Select(index => new CpuCorePerformanceInput(index, 0, null, null, logicalProcessorCountByIndex(index)))
            .ToArray();
    }
}
