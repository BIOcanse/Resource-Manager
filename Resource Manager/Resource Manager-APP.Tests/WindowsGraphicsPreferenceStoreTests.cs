using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGraphicsPreferenceStoreTests
{
    [Fact]
    public void BuildPreferIntegratedGpuValue_AddsPreferenceWhenMissing()
    {
        var store = new WindowsGraphicsPreferenceStore();

        var value = store.BuildPreferIntegratedGpuValue(null);

        Assert.Equal("GpuPreference=1;", value);
        Assert.Equal("节能 GPU / 核显优先", store.DescribePreference(value));
    }

    [Fact]
    public void BuildPreferIntegratedGpuValue_ReplacesHighPerformanceAndPreservesOtherFields()
    {
        var store = new WindowsGraphicsPreferenceStore();

        var value = store.BuildPreferIntegratedGpuValue("GpuPreference=2;AutoHDREnable=1;SwapEffectUpgradeEnable=1;");

        Assert.Equal("GpuPreference=1;AutoHDREnable=1;SwapEffectUpgradeEnable=1;", value);
    }
}
