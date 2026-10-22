using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricCaptureSourceStatusTests
{
    [Theory]
    [InlineData(false, null, NativeMetricSnapshotObservationStatus.Unavailable)]
    [InlineData(true, 1, NativeMetricSnapshotObservationStatus.Unsupported)]
    public void UnroutedSmuCollectionDistinguishesInventoryGapFromKnownWrongAdapter(
        bool inventoryCurrent, int? routedIndex, NativeMetricSnapshotObservationStatus expected)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"smu-route-status-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "AMD integrated", "gpu-amd-integrated", 0xA1, 1, 2, false)],
            [], [], workspace.CreateCatalogHandleMap());
        var binding = Assert.Single(catalog.MetricBindings.Values, row =>
            row.Field?.Name == "gpu_amd_apu_temperature_reported_celsius");
        var at = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, at);
        var runtime = new NativeMetricSnapshotSourceRuntimeFacts(
            static _ => NativeMetricSnapshotZoneMode.Normal, TimeSpan.FromSeconds(5));
        var plan = workspace.PlanCollection([binding.MetricHandle], runtime.CaptureModes(catalog, at),
            includeGpuInventory: false, commandAt: at.AddMilliseconds(1));
        var source = Assert.Single(plan.Sources);
        var completion = WindowsHardwareMetricCapture.CreateUnroutedAmdSmuCompletion(
            plan, source, new(source.SourceHandle, 1, 1), at.AddMilliseconds(1), routedIndex, inventoryCurrent);
        Assert.Equal(NativeMetricSnapshotSourceStatus.Complete, completion.Status);
        Assert.Equal((uint)expected, Assert.Single(completion.Observations).Status);
    }

    [Fact]
    public void LegacyAmdGpuTemperatureUsesEdgeWhileApuTemperatureStaysIndependent()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            compiled,
            [new(0, "AMD integrated", "gpu-amd-integrated", 0xA1, 1, 2, false)],
            [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var apu = Assert.Single(catalog.MetricBindings.Values, row =>
            row.Field?.Name == "gpu_amd_apu_temperature_reported_celsius");
        var edge = Assert.Single(catalog.MetricBindings.Values, row =>
            row.Field?.Name == "gpu_amd_edge_temperature_celsius");
        Assert.NotEqual(apu.MetricHandle, edge.MetricHandle);
        Assert.Empty(apu.LegacyMetricId);
        Assert.Equal(edge.MetricHandle, catalog.LegacyMetricHandles["gpu.0.temperature"]);
        Assert.Equal(NativeMetricSnapshotSources.AmdAdlx, edge.SourceId);
    }

    [Fact]
    public void UnavailableSmuProviderTemporarilySelectsAdlxForSharedAmdGraphicsClock()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
            compiled,
            Path.Combine(Path.GetTempPath(), $"amd-smu-fallback-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            compiled,
            [new(0, "AMD integrated", "gpu-amd-integrated", 0xA1, 1, 2, false)],
            [],
            [],
            workspace.CreateCatalogHandleMap());
        var binding = Assert.Single(catalog.MetricBindings.Values.Where(binding =>
            binding.SourceId == NativeMetricSnapshotSources.AmdSmu
            && binding.FieldCode == MetricFieldCode.GpuGraphicsClock
            && binding.DisplayIndex == 0));
        var adlxRule = Assert.Single(catalog.RuleBindings.Values.Where(rule =>
            rule.SourceId == NativeMetricSnapshotSources.AmdAdlx
            && rule.FieldCode == MetricFieldCode.GpuGraphicsClock
            && rule.DisplayIndex == 0));
        Assert.Equal(binding.MetricHandle, adlxRule.MetricHandle);
        var at = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, at);

        var runtimeFacts = new NativeMetricSnapshotSourceRuntimeFacts(
            static _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.FromSeconds(5));
        var initiallyAvailable = runtimeFacts.CaptureModes(catalog, at);
        var requested = new[] { binding.MetricHandle };
        var firstPlan = workspace.PlanCollection(
            requested,
            initiallyAvailable,
            includeGpuInventory: false,
            commandAt: at.AddMilliseconds(1));
        var smuPlan = Assert.Single(firstPlan.Sources);
        Assert.Equal(NativeMetricSnapshotSources.AmdSmu, catalog.SourceIds[smuPlan.SourceHandle]);

        var transientMissingSensor = WindowsHardwareMetricCapture.CreateAmdSmuCompletion(
            firstPlan,
            smuPlan,
            new(smuPlan.SourceHandle, 1, 1),
            at.AddMilliseconds(1),
            AmdMetrics("Active"),
            integratedGpuIndex: 0,
            gpuInventoryIsCurrent: true);
        Assert.Equal(NativeMetricSnapshotSourceStatus.Complete, transientMissingSensor.Status);
        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            Assert.Single(transientMissingSensor.Observations).Status);

        var unavailable = WindowsHardwareMetricCapture.CreateAmdSmuCompletion(
            firstPlan,
            smuPlan,
            new(smuPlan.SourceHandle, 1, 1),
            at.AddMilliseconds(1),
            AmdMetrics("Unavailable"),
            integratedGpuIndex: 0,
            gpuInventoryIsCurrent: true);
        Assert.Equal(NativeMetricSnapshotSourceStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Observations);
        _ = workspace.CompleteSource(firstPlan, unavailable, at.AddMilliseconds(1));
        runtimeFacts.RecordCompletion(smuPlan.SourceHandle, unavailable.Status, at.AddMilliseconds(1));

        var duringCooldown = runtimeFacts.CaptureModes(catalog, at.AddSeconds(1));
        Assert.Equal(
            NativeMetricSnapshotSourceAvailability.Unavailable,
            Assert.Single(duringCooldown.Where(mode => mode.SourceHandle == smuPlan.SourceHandle)).Availability);
        var fallbackPlan = workspace.PlanCollection(
            requested,
            duringCooldown,
            includeGpuInventory: false,
            commandAt: at.AddSeconds(1));
        var fallback = Assert.Single(fallbackPlan.Sources);
        Assert.Equal(NativeMetricSnapshotSources.AmdAdlx, catalog.SourceIds[fallback.SourceHandle]);

        var afterCooldown = runtimeFacts.CaptureModes(catalog, at.AddSeconds(6));
        Assert.Equal(
            NativeMetricSnapshotSourceAvailability.Available,
            Assert.Single(afterCooldown.Where(mode => mode.SourceHandle == smuPlan.SourceHandle)).Availability);
    }

    [Fact]
    public void RequestCaptureReadsZoneModesOnlyForRequestedRulesAndGpuInventory()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            compiled,
            [new(0, "GPU", "gpu-exact", 1, 1, 1, true)],
            [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var collectionRequest = WindowsHardwareMetricCapture.CreateCollectionRequest(
            MetricSampleRequest.CatalogProbe, catalog, fieldCatalog: null);
        var requested = NativeMetricSnapshotRequestProjection.CreateMetricHandles(
            collectionRequest, catalog);
        var requestedMetricSet = requested.ToHashSet();
        var expectedSources = catalog.RuleBindings.Values
            .Where(rule => requestedMetricSet.Contains(rule.MetricHandle))
            .Select(static rule => rule.SourceHandle)
            .ToHashSet();
        expectedSources.UnionWith(catalog.Sources
            .Where(static source => source.SourceRole
                == (uint)NativeMetricSnapshotSourceRole.GpuInventory)
            .Select(static source => source.SourceHandle));
        var expectedSourceIds = expectedSources
            .Select(handle => catalog.SourceIds[handle])
            .ToHashSet(StringComparer.Ordinal);
        var observedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var facts = new NativeMetricSnapshotSourceRuntimeFacts(sourceId =>
        {
            observedSourceIds.Add(sourceId);
            if (sourceId == NativeMetricSnapshotSources.WindowsCpu)
            {
                throw new InvalidOperationException(
                    "An unrequested CPU source mode must not be queried for a GPU catalog probe.");
            }
            return NativeMetricSnapshotZoneMode.Normal;
        }, TimeSpan.FromSeconds(5));

        var modes = facts.CaptureModesForRequest(
            catalog, DateTimeOffset.UtcNow, requested, includeGpuInventory: true);

        Assert.Equal(catalog.Sources.Length, modes.Length);
        Assert.True(expectedSourceIds.SetEquals(observedSourceIds));
        Assert.Contains(NativeMetricSnapshotSources.PdhGpuEngine, observedSourceIds);
        Assert.DoesNotContain(NativeMetricSnapshotSources.WindowsCpu, observedSourceIds);
    }

    [Fact]
    public void UnsupportedOemFanDoesNotBlockMemoryAndSelectsWmiFallback()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
            compiled,
            Path.Combine(Path.GetTempPath(), $"oem-fan-unsupported-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            compiled, [], [], [], workspace.CreateCatalogHandleMap());
        var startedAt = DateTimeOffset.UtcNow;
        _ = workspace.ReplaceCatalog(catalog, startedAt);
        var fanSourceHandle = Assert.Single(catalog.Sources,
            source => catalog.SourceIds[source.SourceHandle]
                == NativeMetricSnapshotSources.NotebookOemFan).SourceHandle;
        var initialCommitted = workspace.ReadCommitted();
        var initialFan = Assert.Single(initialCommitted.Sources,
            source => source.SourceHandle == fanSourceHandle);
        Assert.Equal(0UL, initialFan.CapabilityGeneration);
        Assert.Equal((uint)NativeMetricSnapshotSourceStatus.Unavailable,
            initialFan.Status);

        var fanMode = NativeMetricSnapshotZoneMode.Normal;
        var runtimeFacts = new NativeMetricSnapshotSourceRuntimeFacts(
            sourceId => sourceId == NativeMetricSnapshotSources.NotebookOemFan
                ? fanMode
                : NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.FromSeconds(5));
        const ulong workspaceIdentity = 11;
        Assert.True(runtimeFacts.NeedsWorkspaceSourceSynchronization(
            workspaceIdentity));
        runtimeFacts.SynchronizeWorkspaceSources(
            workspaceIdentity, catalog, initialCommitted.Sources);
        Assert.False(runtimeFacts.NeedsWorkspaceSourceSynchronization(
            workspaceIdentity));
        Assert.True(runtimeFacts.NeedsWorkspaceSourceSynchronization(
            workspaceIdentity + 1));

        var fanMetric = catalog.MetricHandles["cpu.fanRpm"];
        var fanRequest = new[] { fanMetric };
        var firstModes = runtimeFacts.CaptureModesForRequest(
            catalog, startedAt, fanRequest, includeGpuInventory: false);
        var firstFanMode = Assert.Single(firstModes,
            mode => mode.SourceHandle == fanSourceHandle);
        Assert.Equal(1UL, firstFanMode.CapabilityGeneration);
        Assert.Equal(NativeMetricSnapshotSourceAvailability.Available,
            firstFanMode.Availability);
        var firstPlan = workspace.PlanCollection(
            fanRequest,
            firstModes,
            includeGpuInventory: false,
            commandAt: startedAt.AddMilliseconds(1));
        var firstSource = Assert.Single(firstPlan.Sources);
        Assert.Equal(
            NativeMetricSnapshotSources.NotebookOemFan,
            catalog.SourceIds[firstSource.SourceHandle]);

        var unsupportedAt = startedAt.AddMilliseconds(2);
        var unsupportedCompletion = WindowsHardwareMetricCapture.CreateNotebookFanCompletion(
            firstPlan,
            firstSource,
            runtimeFacts.BeginObservation(firstSource.SourceHandle),
            unsupportedAt,
            new(new HardwareSensorProviderState(
                "recorded OEM fan", "UnsupportedFirmware", "recorded"), null, null, []));
        Assert.Equal(
            NativeMetricSnapshotSourceStatus.Unsupported,
            unsupportedCompletion.Status);
        _ = workspace.CompleteSource(firstPlan, unsupportedCompletion, unsupportedAt);
        runtimeFacts.RecordCompletion(
            firstSource.SourceHandle,
            unsupportedCompletion.Status,
            unsupportedAt);

        var afterUnsupported = runtimeFacts.CaptureModesForRequest(
            catalog,
            startedAt.AddMilliseconds(3),
            fanRequest,
            includeGpuInventory: false);
        Assert.Equal(
            NativeMetricSnapshotSourceAvailability.Unsupported,
            Assert.Single(afterUnsupported.Where(mode =>
                mode.SourceHandle == firstSource.SourceHandle)).Availability);

        var memoryPlan = workspace.PlanCollection(
            [catalog.MetricHandles["memory.usage"]],
            afterUnsupported,
            includeGpuInventory: false,
            commandAt: startedAt.AddMilliseconds(4));
        Assert.Equal(
            NativeMetricSnapshotSources.WindowsMemory,
            catalog.SourceIds[Assert.Single(memoryPlan.Sources).SourceHandle]);

        var fallbackPlan = workspace.PlanCollection(
            fanRequest,
            afterUnsupported,
            includeGpuInventory: false,
            commandAt: startedAt.AddMilliseconds(5));
        Assert.Equal(
            NativeMetricSnapshotSources.HardwareMonitorWmi,
            catalog.SourceIds[Assert.Single(fallbackPlan.Sources).SourceHandle]);
    }

    [Fact]
    public void ImportedUnsupportedOemFanRestoresModeUntilZoneGenerationChanges()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var directory = Path.Combine(
            Path.GetTempPath(), $"oem-fan-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var persistencePath = Path.Combine(directory, "state.bin");
        try
        {
            var fanMode = NativeMetricSnapshotZoneMode.LowPower;
            NativeMetricSnapshotSourceOutput persistedOem;
            using (var original = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
                       compiled, persistencePath))
            {
                var originalCatalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                    compiled, [], [], [], original.CreateCatalogHandleMap());
                var startedAt = DateTimeOffset.UtcNow;
                _ = original.ReplaceCatalog(originalCatalog, startedAt);
                var fanMetric = originalCatalog.MetricHandles["cpu.fanRpm"];
                var originalFanRequest = new[] { fanMetric };
                var facts = new NativeMetricSnapshotSourceRuntimeFacts(
                    sourceId => sourceId == NativeMetricSnapshotSources.NotebookOemFan
                        ? fanMode
                        : NativeMetricSnapshotZoneMode.Normal,
                    TimeSpan.FromSeconds(5));
                facts.SynchronizeWorkspaceSources(
                    21, originalCatalog, original.ReadCommitted().Sources);

                var modes = facts.CaptureModesForRequest(
                    originalCatalog, startedAt, originalFanRequest, includeGpuInventory: false);
                var plan = original.PlanCollection(
                    originalFanRequest,
                    modes,
                    includeGpuInventory: false,
                    commandAt: startedAt.AddMilliseconds(1));
                var sourcePlan = Assert.Single(plan.Sources);
                Assert.Equal(
                    NativeMetricSnapshotSources.NotebookOemFan,
                    originalCatalog.SourceIds[sourcePlan.SourceHandle]);
                var unsupportedAt = startedAt.AddMilliseconds(2);
                var completion = WindowsHardwareMetricCapture.CreateNotebookFanCompletion(
                    plan,
                    sourcePlan,
                    facts.BeginObservation(sourcePlan.SourceHandle),
                    unsupportedAt,
                    new(new HardwareSensorProviderState(
                        "recorded OEM fan", "UnsupportedFirmware", "recorded"), null, null, []));
                _ = original.CompleteSource(plan, completion, unsupportedAt);
                facts.RecordCompletion(
                    sourcePlan.SourceHandle, completion.Status, unsupportedAt);
                _ = original.ExportAndPersist(startedAt.AddMilliseconds(3));

                persistedOem = Assert.Single(original.ReadCommitted().Sources,
                    source => source.SourceHandle == sourcePlan.SourceHandle);
                Assert.Equal(
                    (uint)NativeMetricSnapshotSourceStatus.Unsupported,
                    persistedOem.Status);
            }

            using var restarted = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
                compiled, persistencePath);
            var restoredCatalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                compiled, [], [], [], restarted.CreateCatalogHandleMap());
            _ = restarted.ReplaceCatalog(restoredCatalog, DateTimeOffset.UtcNow);
            var restoredSources = restarted.ReadCommitted().Sources;
            var restoredOemHandle = Assert.Single(restoredCatalog.Sources,
                source => restoredCatalog.SourceIds[source.SourceHandle]
                    == NativeMetricSnapshotSources.NotebookOemFan).SourceHandle;
            var restoredOem = Assert.Single(restoredSources,
                source => source.SourceHandle == restoredOemHandle);
            Assert.Equal(persistedOem.CapabilityGeneration, restoredOem.CapabilityGeneration);
            Assert.Equal(
                (uint)NativeMetricSnapshotSourceStatus.Unsupported,
                restoredOem.Status);

            var restoredFacts = new NativeMetricSnapshotSourceRuntimeFacts(
                sourceId => sourceId == NativeMetricSnapshotSources.NotebookOemFan
                    ? fanMode
                    : NativeMetricSnapshotZoneMode.Normal,
                TimeSpan.FromSeconds(5));
            restoredFacts.SynchronizeWorkspaceSources(
                22, restoredCatalog, restoredSources);
            var fanRequest = new[] { restoredCatalog.MetricHandles["cpu.fanRpm"] };
            var memoryRequest = new[] { restoredCatalog.MetricHandles["memory.usage"] };
            var restoredAt = DateTimeOffset.UtcNow;
            var restoredModes = restoredFacts.CaptureModesForRequest(
                restoredCatalog, restoredAt, memoryRequest, includeGpuInventory: false);
            Assert.Equal(
                NativeMetricSnapshotSourceAvailability.Unsupported,
                Assert.Single(restoredModes.Where(mode =>
                    mode.SourceHandle == restoredOemHandle)).Availability);
            var memoryPlan = restarted.PlanCollection(
                memoryRequest,
                restoredModes,
                includeGpuInventory: false,
                commandAt: restoredAt.AddMilliseconds(1));
            Assert.Equal(
                NativeMetricSnapshotSources.WindowsMemory,
                restoredCatalog.SourceIds[Assert.Single(memoryPlan.Sources).SourceHandle]);

            var fanModes = restoredFacts.CaptureModesForRequest(
                restoredCatalog,
                restoredAt.AddMilliseconds(2),
                fanRequest,
                includeGpuInventory: false);
            var restoredMode = Assert.Single(fanModes,
                mode => mode.SourceHandle == restoredOemHandle);
            Assert.Equal(
                NativeMetricSnapshotSourceAvailability.Unsupported,
                restoredMode.Availability);
            Assert.Equal(persistedOem.CapabilityGeneration,
                restoredMode.CapabilityGeneration);
            var fallbackPlan = restarted.PlanCollection(
                fanRequest,
                fanModes,
                includeGpuInventory: false,
                commandAt: restoredAt.AddMilliseconds(3));
            Assert.Equal(
                NativeMetricSnapshotSources.HardwareMonitorWmi,
                restoredCatalog.SourceIds[Assert.Single(fallbackPlan.Sources).SourceHandle]);

            fanMode = NativeMetricSnapshotZoneMode.Normal;
            var changedModes = restoredFacts.CaptureModesForRequest(
                restoredCatalog,
                restoredAt.AddMilliseconds(4),
                fanRequest,
                includeGpuInventory: false);
            var changedOem = Assert.Single(changedModes,
                mode => mode.SourceHandle == restoredOemHandle);
            Assert.Equal(
                NativeMetricSnapshotSourceAvailability.Available,
                changedOem.Availability);
            Assert.Equal(persistedOem.CapabilityGeneration + 1,
                changedOem.CapabilityGeneration);
            var recoveredPlan = restarted.PlanCollection(
                fanRequest,
                changedModes,
                includeGpuInventory: false,
                commandAt: restoredAt.AddMilliseconds(5));
            Assert.Equal(
                NativeMetricSnapshotSources.NotebookOemFan,
                restoredCatalog.SourceIds[Assert.Single(recoveredPlan.Sources).SourceHandle]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(MetricFieldCode.GpuGraphicsClock)]
    [InlineData(MetricFieldCode.GpuTemperature)]
    [InlineData(MetricFieldCode.GpuVoltage)]
    public void AmdSmuMarksFieldsOutsideTheRoutedIntegratedAdapterUnsupported(
        MetricFieldCode fieldCode)
    {
        var status = WindowsHardwareMetricCapture.AmdSmuMissingValueStatus(
            fieldCode,
            displayIndex: 1,
            integratedGpuIndex: 0,
            gpuInventoryIsCurrent: true);

        Assert.Equal(NativeMetricSnapshotObservationStatus.Unsupported, status);
    }

    [Theory]
    [InlineData(MetricFieldCode.GpuGraphicsClock)]
    [InlineData(MetricFieldCode.GpuTemperature)]
    [InlineData(MetricFieldCode.GpuVoltage)]
    public void MissingValueOnTheRoutedIntegratedAdapterRemainsUnavailable(
        MetricFieldCode fieldCode)
    {
        var status = WindowsHardwareMetricCapture.AmdSmuMissingValueStatus(
            fieldCode,
            displayIndex: 0,
            integratedGpuIndex: 0,
            gpuInventoryIsCurrent: true);

        Assert.Equal(NativeMetricSnapshotObservationStatus.Unavailable, status);
    }

    [Theory]
    [InlineData(MetricFieldCode.CpuTemperature, 0)]
    [InlineData(MetricFieldCode.GpuTemperature, -1)]
    public void UnrelatedOrUnidentifiedFieldsKeepUnavailableStatus(
        MetricFieldCode fieldCode,
        int displayIndex)
    {
        var status = WindowsHardwareMetricCapture.AmdSmuMissingValueStatus(
            fieldCode,
            displayIndex,
            integratedGpuIndex: null,
            gpuInventoryIsCurrent: false);

        Assert.Equal(NativeMetricSnapshotObservationStatus.Unavailable, status);
    }

    [Fact]
    public void UnsupportedCpuFrequencyPercentRetainsItsExistingStatus()
    {
        var status = WindowsHardwareMetricCapture.AmdSmuMissingValueStatus(
            MetricFieldCode.CpuFrequencyPercent,
            displayIndex: -1,
            integratedGpuIndex: null,
            gpuInventoryIsCurrent: false);

        Assert.Equal(NativeMetricSnapshotObservationStatus.Unsupported, status);
    }

    [Fact]
    public void CurrentInventoryWithoutAnAmdIntegratedAdapterMarksGpuSensorsUnsupported()
    {
        var status = WindowsHardwareMetricCapture.AmdSmuMissingValueStatus(
            MetricFieldCode.GpuTemperature,
            displayIndex: 1,
            integratedGpuIndex: null,
            gpuInventoryIsCurrent: true);

        Assert.Equal(NativeMetricSnapshotObservationStatus.Unsupported, status);
    }

    private static CpuSensorMetrics AmdMetrics(string providerState)
        => new(
            new HardwareSensorProviderState("AMD SMU / PawnIO", providerState, "recorded"),
            null,
            null,
            null,
            null);
}
