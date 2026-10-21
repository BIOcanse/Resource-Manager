using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Mechrevo;

namespace Resource_Manager_APP.Tests;

public sealed class FanSensorUnitTests
{
    [Fact]
    public void AdlxCatalogAndProductionRequestUseRpm()
    {
        var template = Assert.Single(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish.RuleTemplates,
            rule => rule.SourceId == "amd.adlx" && rule.TemplateId.Contains("fan", StringComparison.Ordinal));
        Assert.Equal("gpu.{gpu}.fanRpm", template.MetricIdTemplate);
        Assert.Equal((ulong)BitConverter.DoubleToInt64Bits(double.MaxValue), template.MaximumValueBits);
        Assert.True(WindowsHardwareMetricSampler.CreateAdlxReadRequest([MetricFieldTestBindings.Create("gpu.{gpu}.fanRpm", 0)]).IncludeFan);
        Assert.False(WindowsHardwareMetricSampler.CreateAdlxReadRequest([MetricFieldTestBindings.Create("gpu.{gpu}.fanPercent", 0)]).IncludeFan);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(0, 0d)]
    [InlineData(78, 39d)]
    [InlineData(80, 40d)]
    [InlineData(100, 50d)]
    [InlineData(101, 50.5d)]
    [InlineData(200, 100d)]
    [InlineData(201, null)]
    [InlineData(255, null)]
    public void OemPwmUsesOneScaleAcrossItsRange(int? raw, double? expected)
        => Assert.Equal(expected, MechrevoUwAcpiReader.DecodeDutyPercent(raw));

    [Theory]
    [InlineData(0, true, 0d)]
    [InlineData(40, true, 40d)]
    [InlineData(2400, true, 2400d)]
    [InlineData(-1, true, null)]
    [InlineData(2400, false, null)]
    public void AdlxFanSpeedIsOnlyPublishedAsRpm(int rpm, bool available, double? expected)
    {
        var sensors = AmdAdlxBridgeReader.CreateSensors(new NativeAdlxGpuMetrics
        { AvailableFlags = available ? 256 : 0, FanSpeedRpm = rpm }, "Fixture GPU");
        Assert.Equal(expected, sensors.FanSpeedRpm);
        Assert.Null(sensors.FanSpeedPercent);
    }
}
