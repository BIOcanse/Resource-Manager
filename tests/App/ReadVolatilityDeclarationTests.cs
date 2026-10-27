using System.Text.Json;
using System.Reflection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.ReadWrite.Windows.Results.Explicit;
using Xunit;

namespace Resource_Manager_APP.Tests;

public sealed class ReadVolatilityDeclarationTests
{
    [Fact]
    public void ExistingFieldAndNativeSourceDeclarationsRequireExplicitValidVolatility()
    {
        var information = typeof(DeviceInformationDefinition).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.FieldType == typeof(DeviceInformationDefinition))
            .Select(field => Assert.IsType<DeviceInformationDefinition>(field.GetValue(null)));
        foreach (var declaration in information) AssertValid(declaration.Volatility);
        foreach (var declaration in MetricFieldRegistry.Definitions) AssertValid(declaration.Volatility);
        foreach (var declaration in ProcessMetricFieldRegistry.Definitions) AssertValid(declaration.Volatility);

        var sources = typeof(NativeMetricSnapshotSources).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var declaration = source.GetCustomAttribute<RuntimeReadAttribute>();
            Assert.True(declaration is not null, $"Native source {source.Name} has no volatility declaration.");
            var classified = Assert.IsType<RuntimeReadAttribute>(declaration);
            AssertValid(new ReadVolatility(classified.Kind, classified.Event));
        }
    }

    private static void AssertValid(ReadVolatility volatility)
    {
        Assert.NotNull(volatility);
        Assert.True(Enum.IsDefined(volatility.Kind));
        if (volatility.Kind == ReadVolatilityKind.EventDriven) Assert.False(string.IsNullOrWhiteSpace(volatility.Event));
        else Assert.Null(volatility.Event);
    }

    [Fact]
    public void EventDrivenReadsRequireAnEventAndOtherKindsRejectOne()
    {
        Assert.Throws<ArgumentException>(() => ReadVolatility.EventDriven(" "));
        Assert.Throws<ArgumentException>(() => new ReadVolatility(ReadVolatilityKind.Fixed, "changed"));
        Assert.Throws<ArgumentException>(() => new RuntimeReadAttribute(ReadVolatilityKind.Sampled, "changed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadVolatility((ReadVolatilityKind)99));
        var declared = ReadVolatility.EventDriven("producer transition");
        Assert.Equal(declared, JsonSerializer.Deserialize<ReadVolatility>(JsonSerializer.Serialize(declared)));
    }

    [Fact]
    public void ProcessIdentityAndLiveCountersHaveDifferentSourceSemantics()
    {
        Assert.Equal(ReadVolatilityKind.Fixed, ProcessMetricFieldRegistry.Definitions.Single(
            field => field.Code == ProcessMetricFieldCode.StartTime).Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Sampled, ProcessMetricFieldRegistry.Definitions.Single(
            field => field.Code == ProcessMetricFieldCode.WorkingSet).Volatility.Kind);
        // The runtime contract fixes the command line to the observed PID/start-key instance.
        Assert.Equal(ReadVolatilityKind.Fixed, ProcessMetricFieldRegistry.Definitions.Single(
            field => field.Code == ProcessMetricFieldCode.CommandLine).Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Sampled,
            ProcessGpuResourceFieldRegistry.ForResidentMemory(0, 1, ProcessMetricFieldCode.GpuResidentBytes).Volatility.Kind);
    }

    [Fact]
    public void ImmutableDeclarationsDoNotInheritTheVolatilityOfTheirMixedRawSource()
    {
        Assert.Equal(ReadVolatilityKind.Fixed, DeviceInformationFieldRegistry.OsAllBuildDeclared.Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Fixed, DeviceInformationFieldRegistry.OsAllBootTimeActual.Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Sampled, DeviceInformationFieldRegistry.OsAllUptimeActual.Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Fixed, DeviceInformationFieldRegistry.StorageAllNvmeControllerAccessibleTotalNvmCapacityReportedBytes.Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.EventDriven, DeviceInformationFieldRegistry.StorageAllNvmeControllerAccessibleUnallocatedNvmCapacityReportedBytes.Volatility.Kind);
        Assert.False(string.IsNullOrWhiteSpace(DeviceInformationFieldRegistry.StorageAllNvmeControllerAccessibleUnallocatedNvmCapacityReportedBytes.Volatility.Event));
        Assert.Equal(ReadVolatilityKind.Fixed, MetricFieldRegistry.Definitions.Single(
            field => field.Code == MetricFieldCode.IntelPackageEnergyUnit).Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.Sampled, MetricFieldRegistry.Definitions.Single(
            field => field.Code == MetricFieldCode.IntelPackageEnergyCounter).Volatility.Kind);
    }

    [Fact]
    public void OverlayDeclarationsRetainSampledSourceSemantics()
    {
        Assert.All(MetricCatalog.TargetSoftwareDefinitions(null),
            field => Assert.Equal(ReadVolatilityKind.Sampled, field.Volatility.Kind));
    }

    [Theory]
    [InlineData(MetricFieldCode.GpuSharedMemoryUsed, "gpu_all_shared_memory_used_bytes")]
    [InlineData(MetricFieldCode.AdlxVoltage, "gpu_amd_voltage_reported_volts")]
    [InlineData(MetricFieldCode.AdlxDedicatedMemoryMb, "gpu_amd_adlx_dedicated_memory_mb")]
    [InlineData(MetricFieldCode.AdlxNpuActivityPercent, "gpu_amd_npu_activity_percent")]
    [InlineData(MetricFieldCode.AdlxNpuFrequencyMhz, "gpu_amd_npu_frequency_mhz")]
    public void CurrentGpuReadingsPreserveDevNamesAndSampledSemantics(MetricFieldCode code, string name)
    {
        var field = MetricFieldRegistry.Definitions.Single(field => field.Code == code && field.Name == name);
        Assert.Equal(ReadVolatilityKind.Sampled, field.Volatility.Kind);
    }

    [Fact]
    public void ExplicitResultSubmissionAndFrameSamplingDeclareTheirDifferentChanges()
    {
        Assert.All(WindowsExplicitResultFieldRegistry.Definitions, field =>
        {
            Assert.Equal(ReadVolatilityKind.EventDriven, field.Volatility.Kind);
            Assert.Equal("WindowsExplicitResultOwner.Submit accepted result", field.Volatility.Event);
        });
        Assert.Equal(ReadVolatilityKind.Sampled, FrameFieldRegistry.FrameAllPresentIntervalMs.Volatility.Kind);
        Assert.Equal(ReadVolatilityKind.EventDriven, FrameFieldRegistry.FrameAllWindowDurationConfigured.Volatility.Kind);
    }
}
