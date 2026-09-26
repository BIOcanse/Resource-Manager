using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPerformanceScorePresetResolverTests
{
    [Fact]
    public void Resolve_OrdersDesktopNvidiaAndAmdByAbsoluteHierarchyScore()
    {
        var rtx5090 = GpuPerformanceScorePresetResolver.Resolve("NVIDIA GeForce RTX 5090", 32UL << 30, 2400);
        var rtx4090 = GpuPerformanceScorePresetResolver.Resolve("NVIDIA GeForce RTX 4090", 24UL << 30, 2200);
        var rx7900Xtx = GpuPerformanceScorePresetResolver.Resolve("AMD Radeon RX 7900 XTX", 24UL << 30, 2300);
        var rx7600 = GpuPerformanceScorePresetResolver.Resolve("AMD Radeon RX 7600", 8UL << 30, 2000);

        Assert.Equal(14636, rtx5090.RasterScore);
        Assert.Equal(0, rtx5090.GenerationBonusScore);
        Assert.Equal(rtx5090.RasterScore, rtx5090.Score);
        Assert.True(rtx5090.Score > rtx4090.Score);
        Assert.True(rtx4090.Score > rx7900Xtx.Score);
        Assert.True(rx7900Xtx.Score > rx7600.Score);
        Assert.False(rx7600.IsIntegrated);
        Assert.Equal("3dmark:steel-nomad-dx12:2026-09-13", rx7900Xtx.Source);
    }

    [Fact]
    public void Resolve_KeepsLaptopDgpuBelowDesktopSameNumberButNotIntegrated()
    {
        var desktop = GpuPerformanceScorePresetResolver.Resolve("NVIDIA GeForce RTX 5060", 8UL << 30, 2500);
        var laptop = GpuPerformanceScorePresetResolver.Resolve("NVIDIA GeForce RTX 5060 Laptop GPU", 8UL << 30, 2200);

        Assert.True(desktop.Score > laptop.Score);
        Assert.False(laptop.IsIntegrated);
        Assert.Equal("NVIDIA GeForce RTX 5060 (notebook)", laptop.MatchedPreset);
    }

    [Fact]
    public void Resolve_ClassifiesAmdIntegratedGpuSeparatelyFromScore()
    {
        var integrated = GpuPerformanceScorePresetResolver.Resolve("AMD Radeon(TM) 610M", 0, 600);
        var lowEndDgpu = GpuPerformanceScorePresetResolver.Resolve("NVIDIA GeForce RTX 4050 Laptop GPU", 6UL << 30, 1900);

        Assert.True(integrated.IsIntegrated);
        Assert.True(integrated.IsPresetMatch);
        Assert.Equal(122, integrated.Score);
        Assert.Equal("3dmark:steel-nomad-dx12:computerbase:andi_sco:2024-05-23", integrated.Source);
        Assert.False(lowEndDgpu.IsIntegrated);
        Assert.True(lowEndDgpu.Score > integrated.Score);
        Assert.True(GpuPerformanceScorePresetResolver.Resolve("AMD Radeon 780M (desktop)", 4UL << 30, 2000).IsIntegrated);
    }

    [Fact]
    public void MissingMemoryMeasurementDoesNotMakeKnownDiscreteGpuIntegrated()
    {
        Assert.False(GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName("AMD Radeon Pro VII"));
        Assert.False(GpuPerformanceScorePresetResolver.Resolve("AMD Radeon Pro VII", 0, 0).IsIntegrated);
        Assert.True(GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName("AMD Radeon(TM) 610M"));
        Assert.True(GpuPerformanceScorePresetResolver.Resolve("AMD Radeon(TM) 610M", 0, 0).IsIntegrated);
    }

    [Fact]
    public void Resolve_UsesFallbackForUnknownDgpuWithoutClassifyingNvidiaAsIntegrated()
    {
        var result = GpuPerformanceScorePresetResolver.Resolve("NVIDIA RTX B3000 Experimental", 6UL << 30, 1200);

        Assert.False(result.IsPresetMatch);
        Assert.False(result.IsIntegrated);
        Assert.Equal("unavailable:steel-nomad-dx12", result.Source);
        Assert.Equal(0, result.Score);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("NVIDIA GeForce RTX 3080")]
    [InlineData("NVIDIA GeForce RTX 5060 Laptop GPU")]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition")]
    [InlineData("AMD Radeon PRO W7900")]
    [InlineData("AMD Radeon(TM) 610M")]
    [InlineData("NVIDIA RTX B3000 Experimental")]
    public void Resolve_LegacyUseCasesCannotAlterRasterOrAddBonuses(string model)
    {
        var general = GpuPerformanceScorePresetResolver.Resolve(model, 8UL << 30, 2000);
        string[][] legacyChoices = [[], ["general"], ["gaming"], ["ai"], ["ai", "gaming"], ["unknown"]];
        foreach (var choices in legacyChoices)
        {
            var result = GpuPerformanceScorePresetResolver.Resolve(model, 8UL << 30, 2000, choices);
            Assert.Equal(general.Score, result.Score);
            Assert.Equal(result.RasterScore, result.Score);
            Assert.Equal(0, result.GenerationBonusScore);
            Assert.Equal(0, result.UseCaseBonusScore);
            Assert.Equal([AppGpuPerformanceUseCases.General], result.GpuPerformanceUseCases);
        }
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", 3182)]
    [InlineData("AMD Radeon Graphics (Raphael)", 121)]
    [InlineData("AMD Radeon(TM) 780M", 467)]
    [InlineData("NVIDIA GeForce RTX 5090", 14636)]
    [InlineData("NVIDIA GeForce RTX 5090 Ti", 0)]
    [InlineData("NVIDIA GeForce RTX 2060", 0)]
    public void Resolve_UsesExactRecordedRasterOrUnknownWithoutInventingAVariant(string model, double expected)
    {
        var result = GpuPerformanceScorePresetResolver.Resolve(model, 32UL << 30, 9999);
        Assert.Equal(expected, result.Score);
        Assert.Equal(expected > 0, result.IsPresetMatch);
        Assert.Equal(expected, GpuPerformanceScorePresetResolver.Resolve(model, 0, 0).Score);
    }

    [Theory]
    [InlineData(66.66)]
    [InlineData(0)]
    [InlineData(9000)]
    public void CompiledPlan_UsesManualValueUnchangedAndResetReturnsBenchmark(double manual)
    {
        var gpu = new GpuMetrics(1, "NVIDIA GeForce RTX 3070", 0, 0, 0, 0, 0, 0, 0, 0, null!);
        var defaults = CompiledHardwareScorePlan.Default;
        var overridden = defaults with
        {
            GpuPerformanceScoresByGpuId = new Dictionary<string, double> { ["gpu:1"] = manual }
        };
        Assert.Equal(manual, overridden.ResolveGpuPerformanceScore(gpu));
        Assert.Equal(3182, defaults.ResolveGpuPerformanceScore(gpu));
    }
}
