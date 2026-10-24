using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotNotebookOemFanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WmiFailureDoesNotControlExplicitOemChannelsAndRetentionKeepsOnlyWmiHistory(bool enabled)
    {
        var read = WindowsNotebookFanProjection.CreateFanCatalog(new(false, []), enabled, 10);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, read.Status);
        Assert.Equal(enabled ? 2 : 0, read.Identities.Count);
        Assert.All(read.Identities, identity => Assert.NotNull(identity.NotebookOemRole));
        var plan = NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot);
        var previous = NativeMetricSnapshotCatalogProjection.Create(plan, [], [],
            new[] { new NativeMetricSnapshotOrdinalCatalogIdentity(7, "wmi|fixture|fan0", 9) }
                .Concat(WindowsNotebookFanProjection.CreateCatalogIdentities(8, 9)).ToArray(),
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(10,
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], read.Status, read.Identities);
        var retained = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(topology, previous);
        var wmi = Assert.Single(retained.SystemFans, identity => identity.NotebookOemRole is null && identity.NvmlFan is null);
        Assert.Equal("wmi|fixture|fan0", wmi.ExactIdentityKey);
        Assert.Equal(0, wmi.DisplayIndex);
        var oem = retained.SystemFans.Where(static identity => identity.NotebookOemRole is not null).ToArray();
        Assert.Equal(enabled ? new[] { 1, 2 } : [], oem.Select(static identity => identity.DisplayIndex));
        Assert.Equal(enabled ? new[] { WindowsNotebookFanProjection.CpuFanIdentityKey, WindowsNotebookFanProjection.DiscreteGpuFanIdentityKey } : [],
            oem.Select(static identity => identity.ExactIdentityKey));
        Assert.Equal(enabled ? new NotebookOemPhysicalFanRole?[] { NotebookOemPhysicalFanRole.Cpu, NotebookOemPhysicalFanRole.DiscreteGpu } : [],
            oem.Select(static identity => identity.NotebookOemRole));
        Assert.All(retained.SystemFans, identity =>
        {
            Assert.Equal(10UL, identity.TopologyGeneration);
            Assert.Null(identity.NvmlFan);
        });
        var projected = WindowsMetricCatalogTopologyPublication.ProjectCatalog(plan, retained, previous.HandleMap);
        Assert.All(previous.SystemFanIdentities, identity => Assert.Equal(9UL, identity.TopologyGeneration));
        Assert.Equal(retained.SystemFans.ToArray(), projected.SystemFanIdentities.ToArray());
        Assert.Equal(enabled ? 2 : 0, projected.SystemFanIdentities.Count(static identity => identity.NotebookOemRole is not null));
        Assert.All(projected.RuleBindings.Values.Where(binding => binding.DeviceIdentity == wmi.ExactIdentityKey),
            binding => Assert.Equal(NativeMetricSnapshotSourceCatalog.HardwareMonitorWmi, binding.SourceId));
        foreach (var identity in oem)
        {
            var bindings = projected.RuleBindings.Values.Where(binding => binding.DeviceIdentity == identity.ExactIdentityKey).ToArray();
            Assert.NotEmpty(bindings);
            Assert.All(bindings, binding => Assert.Equal(NativeMetricSnapshotSourceCatalog.NotebookOemFan, binding.SourceId));
        }
        var previousWmi = Assert.Single(previous.RuleBindings.Values, binding => binding.DeviceIdentity == wmi.ExactIdentityKey
            && binding.FieldCode == MetricFieldCode.FanRpm);
        var currentWmi = Assert.Single(projected.RuleBindings.Values, binding => binding.DeviceIdentity == wmi.ExactIdentityKey
            && binding.FieldCode == MetricFieldCode.FanRpm);
        Assert.Equal(previousWmi.ScopeHandle, currentWmi.ScopeHandle);
        Assert.Equal(previousWmi.MetricHandle, currentWmi.MetricHandle);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, retained.FanStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, retained.NotebookOemFanStatus);
    }

    [Fact]
    public void TemporaryMissingCpuFan_DoesNotDisableFollowingSubscribedSamples()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"fan-recovery-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
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
                new(new("test", HardwareSensorProviderStates.Active, null), values[round], 50, []));
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
                    or "hardware-monitor.core")
            .Select(static policy => policy.Priority)
            .ToArray();

        Assert.Equal(80U, oem.Priority);
        Assert.All(
            fallbackPriorities,
            priority => Assert.True(oem.Priority < priority));
    }

    [Theory]
    [InlineData(HardwareSensorProviderStates.Active, 1U)]
    [InlineData(HardwareSensorProviderStates.UnsupportedFirmware, 4U)]
    [InlineData(HardwareSensorProviderStates.Unavailable, 3U)]
    [InlineData(HardwareSensorProviderStates.Missing, 3U)]
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
            new HardwareSensorProviderState("test", HardwareSensorProviderStates.Active, "test"),
            3100,
            50,
            [gpu]);

        Assert.Equal(
            3100,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("cpu.fanRpm"),
                sensors,
                1,
                gpu));
        Assert.Equal(
            3024,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("gpu.{gpu}.fanRpm", 1),
                sensors,
                1,
                gpu));
        Assert.Equal(
            48,
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("gpu.{gpu}.fanPercent", 1),
                sensors,
                1,
                gpu));
        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("gpu.{gpu}.fanRpm", 0),
                sensors,
                1,
                gpu));
        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("gpu.{gpu}.temperature", 1),
                sensors,
                1,
                gpu));
    }

    [Fact]
    public void MechrevoPhysicalFanRolesExposeDistinctRpmAndDutyWithoutChangingAggregates()
    {
        var gpu = new PlatformGpuSensor(0, "dGPU fan", "test", null, null, 3200, 64, null, null);
        var sensors = new NotebookOemFanSensorSnapshot(
            new HardwareSensorProviderState("Mechrevo UWACPI", HardwareSensorProviderStates.Active, "test"),
            3000,
            52,
            [gpu])
        {
            PhysicalFans =
            [
                new(WindowsNotebookFanProjection.CpuFanIdentityKey, NotebookOemPhysicalFanRole.Cpu, 3000, 52),
                new(WindowsNotebookFanProjection.DiscreteGpuFanIdentityKey, NotebookOemPhysicalFanRole.DiscreteGpu, 3200, 64)
            ]
        };
        var cpuRpm = MetricFieldTestBindings.Create("fan.{fan}.speed") with
        { DeviceIdentity = WindowsNotebookFanProjection.CpuFanIdentityKey };
        var gpuDuty = MetricFieldTestBindings.Create("fan.{fan}.pwmDutyReported") with
        { DeviceIdentity = WindowsNotebookFanProjection.DiscreteGpuFanIdentityKey };

        Assert.Equal(3000, WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
            MetricFieldTestBindings.Create("cpu.fanRpm"), sensors, 1, gpu));
        Assert.Equal(3200, WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
            MetricFieldTestBindings.Create("gpu.{gpu}.fanRpm", 1), sensors, 1, gpu));
        Assert.Equal(3000, WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(cpuRpm, sensors, 1, gpu));
        Assert.Equal(64, WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(gpuDuty, sensors, 1, gpu));
        Assert.Null(WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
            gpuDuty with { DeviceIdentity = "mechrevo|uwacpi|fan|unknown" }, sensors, 1, gpu));

        var catalogIdentities = WindowsNotebookFanProjection.CreateCatalogIdentities(4, 9);
        Assert.Equal([4, 5], catalogIdentities.Select(static identity => identity.DisplayIndex));
        Assert.Equal(
            [WindowsNotebookFanProjection.CpuFanIdentityKey, WindowsNotebookFanProjection.DiscreteGpuFanIdentityKey],
            catalogIdentities.Select(static identity => identity.ExactIdentityKey));
        Assert.Equal(
            new NotebookOemPhysicalFanRole?[]
            { NotebookOemPhysicalFanRole.Cpu, NotebookOemPhysicalFanRole.DiscreteGpu },
            catalogIdentities.Select(static identity => identity.NotebookOemRole));

        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"fan-roles-{Guid.NewGuid():N}.bin"));
        var allFanIdentities = new[]
            { new NativeMetricSnapshotOrdinalCatalogIdentity(0, "wmi|fixture|fan0", 9) }
            .Concat(catalogIdentities).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            compiled, [], [], allFanIdentities, workspace.CreateCatalogHandleMap());
        Assert.Equal(2, catalog.SystemFanIdentities.Count(static identity => identity.NotebookOemRole is not null));
        foreach (var identity in catalogIdentities)
        {
            Assert.All(catalog.RuleBindings.Values.Where(binding =>
                    binding.DeviceIdentity == identity.ExactIdentityKey),
                binding => Assert.Equal(NativeMetricSnapshotSourceCatalog.NotebookOemFan, binding.SourceId));
            Assert.Contains(catalog.RuleBindings.Values, binding =>
                binding.SourceId == NativeMetricSnapshotSourceCatalog.NotebookOemFan
                && binding.DeviceIdentity == identity.ExactIdentityKey
                && binding.FieldCode == MetricFieldCode.FanRpm);
            Assert.Contains(catalog.RuleBindings.Values, binding =>
                binding.SourceId == NativeMetricSnapshotSourceCatalog.NotebookOemFan
                && binding.DeviceIdentity == identity.ExactIdentityKey
                && binding.FieldCode == MetricFieldCode.FanReportedPwmDuty);
        }
        Assert.DoesNotContain(catalog.RuleBindings.Values, binding =>
            binding.SourceId == NativeMetricSnapshotSourceCatalog.NotebookOemFan
            && binding.DeviceIdentity == "wmi|fixture|fan0");
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
            new HardwareSensorProviderState("test", HardwareSensorProviderStates.Active, "test"),
            null,
            null,
            [gpu]);

        Assert.Null(
            WindowsHardwareMetricSampler.ResolveNotebookOemFanValue(
                MetricFieldTestBindings.Create("gpu.{gpu}.fanRpm", 1),
                sensors,
                null,
                null));
    }
}
