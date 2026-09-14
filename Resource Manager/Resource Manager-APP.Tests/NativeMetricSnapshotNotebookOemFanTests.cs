using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotNotebookOemFanTests
{
    [Fact]
    public void TemporaryMissingCpuFan_DoesNotDisableFollowingSubscribedSamples()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = new NativeMetricSnapshotWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"fan-recovery-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotCatalogProjection.Create(
            compiled, [], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(
            source.SourceHandle, 1, NativeMetricSnapshotZoneMode.Normal,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        double?[] values = [4045, null, 4200];
        for (var round = 0; round < values.Length; round++)
        {
            var capturedAt = DateTimeOffset.UtcNow.AddSeconds(round + 1);
            var plan = workspace.PlanCollection([catalog.MetricHandles["cpu.fanRpm"]],
                modes, includeGpuInventory: false, capturedAt);
            var selected = Assert.Single(plan.Metrics.Where(metric =>
                (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0));
            Assert.Equal(NativeMetricSnapshotSourceCatalog.NotebookOemFan,
                catalog.SourceIds[selected.SourceHandle]);
            var source = Assert.Single(plan.Sources.Where(item => item.SourceHandle == selected.SourceHandle));
            var completion = WindowsHardwareMetricSampler.CreateNotebookFanCompletion(
                plan, source, new(selected.SourceHandle, 1, (ulong)round + 1), capturedAt,
                new(new("test", "Active", null), values[round], 50, []));
            Assert.Equal(values[round] is null
                    ? NativeMetricSnapshotObservationStatus.Unavailable
                    : NativeMetricSnapshotObservationStatus.Current,
                (NativeMetricSnapshotObservationStatus)Assert.Single(completion.Observations).Status);
            workspace.CompleteSource(plan, completion, capturedAt);
        }
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(
            workspace.ReadCommitted(), "test", "test", 1);
        Assert.Equal(4200, snapshot.Items["cpu.fanRpm"].NumericValue);
        Assert.Equal(SamplingObservationStatus.Current,
            snapshot.Datasets["cpu.fanRpm"].Status);
    }

    [Fact]
    public void DefaultProfile_PublishesOemFanAsExplicitPrimarySource()
    {
        var policies = HostManagerTestPlanFactory.CreatePlan()
            .MetricSnapshot.HotPublish.SourcePolicies;
        var oem = policies.Single(static policy =>
            policy.SourceId == NativeMetricSnapshotSourceCatalog.NotebookOemFan);
        var fallbackPriorities = policies
            .Where(static policy =>
                policy.SourceId is "nvidia.nvapi"
                    or "nvidia.nvml"
                    or "amd.adlx"
                    or "hardware-monitor.wmi")
            .Select(static policy => policy.Priority)
            .ToArray();

        Assert.Equal(80U, oem.Priority);
        Assert.All(
            fallbackPriorities,
            priority => Assert.True(oem.Priority < priority));
    }

    [Theory]
    [InlineData("Active", 1U)]
    [InlineData("UnsupportedFirmware", 4U)]
    [InlineData("Unavailable", 3U)]
    [InlineData("Missing", 3U)]
    public void ProviderState_IsProjectedWithoutInventingCompleteness(
        string state,
        uint expected)
    {
        var provider = new HardwareSensorProviderState(
            "test",
            state,
            state);

        Assert.Equal(
            expected,
            (uint)WindowsHardwareMetricSampler
                .ResolveNotebookOemProviderStatus(provider));
    }

    [Fact]
    public void SingleOemGpuFan_IsBoundOnlyToExactDedicatedDisplaySlot()
    {
        var gpu = new PlatformGpuSensor(
            0,
            "dGPU fan",
            "test",
            null,
            null,
            3024,
            48,
            null,
            null);
        var sensors = new NotebookOemFanSensorSnapshot(
            new HardwareSensorProviderState("test", "Active", "test"),
            3100,
            50,
            [gpu]);

        Assert.Equal(
            3100,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "cpu.fanRpm",
                sensors,
                1,
                gpu));
        Assert.Equal(
            3024,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "gpu.1.fanRpm",
                sensors,
                1,
                gpu));
        Assert.Equal(
            48,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "gpu.1.fanPercent",
                sensors,
                1,
                gpu));
        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "gpu.0.fanRpm",
                sensors,
                1,
                gpu));
        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "gpu.1.temperature",
                sensors,
                1,
                gpu));
    }

    [Fact]
    public void AmbiguousOemGpuFan_DoesNotGuessADisplaySlot()
    {
        var gpu = new PlatformGpuSensor(
            0,
            "dGPU fan",
            "test",
            null,
            null,
            3024,
            48,
            null,
            null);
        var sensors = new NotebookOemFanSensorSnapshot(
            new HardwareSensorProviderState("test", "Active", "test"),
            null,
            null,
            [gpu]);

        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                "gpu.1.fanRpm",
                sensors,
                null,
                null));
    }
}
