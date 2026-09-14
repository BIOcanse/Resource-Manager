using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    public async Task FullAutomaticGpuCycleUsesSavedApiAndRestoresActualDeviceSelection(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, false);

    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    public async Task FullFirstUsePublicGpuCycleIdentifiesAndConfiguresThenReusesActualApi(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, false, firstUse: true);

    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    public async Task FirstUseScheduledD3dCycleIdentifiesConfiguresAndRestores(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, false, firstUse: true, scheduledFirstUse: true);

    [VulkanNativeTheory]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task FirstUseScheduledVulkanCycleIdentifiesConfiguresAndRestores(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, false, firstUse: true, scheduledFirstUse: true);

    [VulkanNativeTheory]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task FullFirstUsePublicVulkanCycleIdentifiesAndConfiguresThenRetainsActualApi(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, false, firstUse: true);

    [VulkanNativeTheory]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task FullFirstUsePublicExistingVulkanLayerCycleIdentifiesAndConfiguresThenRetainsActualApi(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, true, firstUse: true);

    [VulkanNativeTheory]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task FullFirstUsePublicMixedCandidatesCycleUsesBothNativeBindingsWithoutInferringAnUnusedApi(string api, GpuGraphicsApi expectedApi)
        => await RunAutomaticGpuCycleAsync(api, expectedApi, true, firstUse: true, mixedCandidates: true);

    [VulkanNativeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullAutomaticVulkanCycleUsesTheSameLedgerAndNormalRestoration(bool existingLayer)
        => await RunAutomaticGpuCycleAsync("vulkan", GpuGraphicsApi.Vulkan, existingLayer);

    private async Task RunAutomaticGpuCycleAsync(string api, GpuGraphicsApi expectedApi, bool existingLayer, bool firstUse = false,
        bool mixedCandidates = false, bool scheduledFirstUse = false)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var child = await GpuPlacementInjectorNativeTests.OwnedProbe.StartAsync(api, output,
            existingLayer ? Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName) : null,
            api == "vulkan" ? Environment.GetEnvironmentVariable("RM_GPU_VULKAN_NATIVE_PROBE") : null, mixedCandidates);
        var inventory = new WindowsGpuAdapterOrderReader().ReadInventory();
        Assert.Equal(SamplingObservationStatus.Current, inventory.Status);
        Assert.Equal(0U, inventory.SkippedCount);
        Assert.Equal(0U, inventory.OverflowCount);
        var initialAdapter = Assert.Single(inventory.Adapters, adapter => NativePdhAdapterIdentity.Pack(adapter.Luid) == child.AdapterLuid);
        var targetAdapter = inventory.Adapters.First(adapter => NativePdhAdapterIdentity.Pack(adapter.Luid) != child.AdapterLuid);
        var targetKey = NativePdhAdapterIdentity.Pack(targetAdapter.Luid);
        const string softwareId = "software:owned-native-gpu-cycle";
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(GpuPlacementPolicyDefaults.CreateSoftwarePolicy(softwareId, "Owned GPU cycle") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto,
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
            RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
            TargetGpu = GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(targetAdapter.Name)
                ? GpuPlacementTargets.IntegratedGpu : GpuPlacementTargets.HighPerformanceGpu
        });
        var facts = CreateNativeGpuCycleFacts(child.Identity, softwareId, inventory, initialAdapter.Index);
        var ledgerPath = Path.Combine(files.Root, "gpu-cycle-owner.json");
        using var ledger = new JsonHostManagerRollbackStateStore(ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1));
        await ledger.ReserveNativeHostSessionIncarnationAsync(default);
        CapturingGpuCycleActions actions = null!;
        RunningGpuPlacementIdentityTests.HistoryReader history = null!;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, processFactsSnapshot: facts, policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, rollbackStateStoreOverride: ledger,
            processRecoveryRead: pid =>
            {
                Assert.Equal(child.Identity.ProcessId, pid);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid,
                    DateTimeOffset.FromFileTime(checked((long)child.Identity.ProcessStartKey))));
            },
            runningGpuActionsFactory: (root, plans) =>
            {
                var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(root));
                var committedHistory = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(root));
                history = new(committedHistory);
                actions = new(new WindowsRunningGpuPlacementActionService(
                    NullLogger<WindowsRunningGpuPlacementActionService>.Instance, runtime,
                    new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance), firstUse ? committedHistory : history, plans, new()),
                    ledger, runtime);
                return actions;
            });
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, child.AdapterLuid));
        var identification = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixture.Root));
        if (existingLayer && !firstUse)
        {
            await using var firstRun = await GpuPlacementInjectorNativeTests.OwnedProbe.StartAsync("vulkan", output,
                executablePath: Environment.GetEnvironmentVariable("RM_GPU_VULKAN_NATIVE_PROBE"));
            var firstObservation = await identification.SaveFirstGraphicsApiAsync(softwareId, "Owned GPU cycle",
                firstRun.Identity, GpuGraphicsApi.Vulkan, default);
            Assert.Equal(GpuGraphicsApi.Vulkan, Assert.Single(firstObservation.Processes).GraphicsApi);
            await firstRun.StopAsync();
            identification = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixture.Root));
        }
        // Only the retained saved-route mode seeds history; first use must obtain a real observation.
        var observed = existingLayer || firstUse
            ? await identification.GetSoftwareHistoryAsync(softwareId, "Owned GPU cycle", default)
            : await identification.SaveFirstGraphicsApiAsync(softwareId, "Owned GPU cycle", child.Identity, expectedApi, default);
        if (firstUse) Assert.Empty(observed.Processes);
        else Assert.Equal(expectedApi, Assert.Single(observed.Processes).GraphicsApi);
        var historyPath = Path.Combine(fixture.Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
        byte[]? savedHistory = firstUse ? null : File.ReadAllBytes(historyPath);
        actions.BeforeObservationWait = async identity =>
        {
            Assert.True(firstUse);
            Assert.Equal(child.Identity, identity);
            Assert.Equal(!existingLayer || mixedCandidates, child.HasProvider());
            Assert.Empty((await identification.GetSoftwareHistoryAsync(softwareId, "Owned GPU cycle", default)).Processes);
            Assert.False(GpuActionFacts.HasPlacementEffects((await ledger.LoadAsync(default)).AppliedPlacements));
            Assert.Equal(child.AdapterLuid, await child.CreateDeviceAsync());
        };
        async Task Cycle()
        {
            if (scheduledFirstUse)
            {
                // Match the hosted loop's gate ownership and scheduled wrapper, not the unowned test shortcut.
                var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
                await gate.WaitAsync();
                try
                {
                    var scheduled = typeof(HostManagerSmartCoordinator).GetMethod("RunScheduledNativeCycleAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task)scheduled.Invoke(fixture.Coordinator, [null, CancellationToken.None])!;
                }
                finally { gate.Release(); }
                return;
            }
            if (firstUse && !scheduledFirstUse) _ = await fixture.Coordinator.RunOnceAsync(default);
            else _ = await fixture.RunRealtimeCycleAsync();
        }
        var enabledPlan = fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy> { [softwareId] = policy },
                new Dictionary<string, ResolvedGpuPlacementPolicy>())
        };
        fixture.RuntimePlanProvider.Publish(enabledPlan);

        fixture.ProcessFacts.SetSnapshot(facts with
        {
            RequestedMetricMask = SchedulingProcessMetricMask.RuntimeState,
            CurrentMetricMask = SchedulingProcessMetricMask.RuntimeState,
            DatasetObservations = facts.DatasetObservations.Where(item => item.Key == SchedulingProcessMetricMask.RuntimeState)
                .ToDictionary(item => item.Key, item => item.Value),
            Processes = facts.Processes.Select(process => process with
            {
                ValidMetricMask = SchedulingProcessMetricMask.RuntimeState, Gpus = []
            }).ToArray()
        });
        await Cycle();
        Assert.Equal(0, actions.ApplyCount);
        Assert.False(child.HasProvider());
        Assert.Empty((await ledger.LoadAsync(default)).AppliedPlacements);
        Assert.Null(fixture.Coordinator.SchedulingAuthority.Compute?.Gpu);
        fixture.ProcessFacts.SetSnapshot(facts);

        Assert.False(child.HasProvider());
        await Cycle();

        Assert.Equal(1, actions.ApplyCount);
        if (firstUse)
        {
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ObservationWaits);
            Assert.Equal(expectedApi, Assert.Single((await identification.GetSoftwareHistoryAsync(
                softwareId, "Owned GPU cycle", default)).Processes).GraphicsApi);
            savedHistory = File.ReadAllBytes(historyPath);
        }
        Assert.Equal(!existingLayer || mixedCandidates, child.HasProvider());
        if (!firstUse) Assert.Equal(softwareId, history.ReadSoftwareId);
        var applied = Assert.Single((await ledger.LoadAsync(default)).AppliedPlacements);
        var record = Assert.Single(applied.Records, item => item.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
        var remoteCalls = applied.Records.Where(GpuRemoteCallRecord.IsActionFact).Select(item =>
        {
            Assert.True(GpuRemoteCallRecord.TryRead(item, out var fact));
            Assert.Equal(child.Identity, fact.Request.Process);
            Assert.True(fact.Result!.Completed);
            Assert.False(fact.BlocksProcess);
            return fact;
        }).ToArray();
        Assert.Equal(existingLayer
            ? new[] { GpuRemoteCallKind.ConfigureProvider, GpuRemoteCallKind.ReadDevices, GpuRemoteCallKind.ReadDevices }
            : firstUse ? new[] { GpuRemoteCallKind.ConfigureProvider, GpuRemoteCallKind.ReadDevices, GpuRemoteCallKind.ReadDevices }
            : new[] { GpuRemoteCallKind.LoadProvider, GpuRemoteCallKind.ConfigureProvider, GpuRemoteCallKind.ReadDevices, GpuRemoteCallKind.ReadDevices },
            actions.CallIds.Select(id => Assert.Single(remoteCalls, call => call.Request.CallId == id).Request.Kind));
        if (firstUse)
        {
            Assert.Equal(mixedCandidates
                ? new[] { GpuRemoteCallKind.LoadObservationProvider, GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StartApiObservation,
                    GpuRemoteCallKind.StopApiObservation, GpuRemoteCallKind.StopApiObservation }
                : existingLayer
                ? new[] { GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StopApiObservation }
                : new[] { GpuRemoteCallKind.LoadObservationProvider, GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StopApiObservation },
                actions.ObservationCallIds.Select(id => Assert.Single(remoteCalls, call => call.Request.CallId == id).Request.Kind));
            Assert.Equal(mixedCandidates ? 8 : existingLayer ? 5 : 6, remoteCalls.Length);
            if (mixedCandidates)
            {
                var stops = actions.ObservationCallIds.Select(id => Assert.Single(remoteCalls, call => call.Request.CallId == id))
                    .Where(call => call.Request.Kind == GpuRemoteCallKind.StopApiObservation).ToArray();
                Assert.Equal(2, stops.Length);
                Assert.True(GpuApiObservationProtocol.TryDecode(stops[0].Result!.Response!, GpuGraphicsApi.Vulkan, out var layerObservation));
                Assert.True(GpuApiObservationProtocol.TryDecode(stops[1].Result!.Response!, GpuGraphicsApi.D3D11, out var commonObservation));
                Assert.Equal(new GpuApiObservationSnapshot(GpuGraphicsApi.Vulkan, false), layerObservation);
                Assert.Equal(new GpuApiObservationSnapshot(0, false), commonObservation);
            }
        }
        Assert.Equal(HostManagerAppliedRecordKinds.GpuShimPolicy, record.Kind);
        Assert.True(GpuShimPolicyRecord.TryRead(record, out var owned));
        Assert.Null(owned.PreviousValue);
        Assert.Equal(targetKey, actions.Plan!.Request.TargetAdapterKey);
        Assert.Equal(expectedApi, actions.Plan.GraphicsApis[child.Identity.ProcessId]);
        var result = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata!["runtimeActionResult"])!;
        var processResult = Assert.Single(result.Processes);
        Assert.True(processResult.Configured);
        Assert.Equal(child.Identity, processResult.Identity);
        Assert.True(processResult.Before!.Success);
        Assert.Equal(processResult.Before.Snapshot, processResult.After!.Snapshot);
        Assert.Equal(RunningGpuPlacementActionStatuses.Prepared, result.Status);
        Assert.False(result.Applied);
        var canonical = fixture.Coordinator.SchedulingAuthority.Compute!.Gpu!.Scores
            .Where(score => score.Kind == NativeComputeScoringOutputKind.ProcessGpu && score.ProcessId == child.Identity.ProcessId)
            .Max(score => score.Score);
        var workspace = ReadPrivateField<NativePlacementCoordinatorWorkspace>(fixture.Coordinator, "placementCoordinatorWorkspace")!;
        Assert.Equal(checked((uint)Math.Round(canonical, MidpointRounding.AwayFromZero)), workspace.Desired[0].Priority);
        Assert.Equal((uint)NativePlacementResourceKind.Gpu, workspace.Desired[0].ResourceKind);
        Assert.Equal(child.Identity.ProcessStartKey, workspace.Desired[0].ProcessStartKey);
        var onTarget = await child.CreateDeviceAsync();
        Assert.Equal(targetKey, onTarget);
        Assert.NotEqual(child.AdapterLuid, onTarget);
        var retainedLedger = File.ReadAllBytes(ledgerPath);

        await Cycle();
        Assert.Equal(1, actions.ApplyCount);
        Assert.Equal(firstUse ? 1 : 0, actions.FirstUseCalls);
        Assert.Equal(retainedLedger, File.ReadAllBytes(ledgerPath));
        Assert.Equal(savedHistory, File.ReadAllBytes(historyPath));

        fixture.RuntimePlanProvider.Publish(enabledPlan with
        {
            Version = enabledPlan.Version + 1,
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
        });
        await Cycle();
        var restoredPlacements = (await ledger.LoadAsync(default)).AppliedPlacements;
        Assert.False(GpuActionFacts.HasPlacementEffects(restoredPlacements));
        Assert.False(GpuActionFacts.HasUnsettledActions(restoredPlacements));
        Assert.Equal(remoteCalls.Select(call => call.RecordId).Order(StringComparer.Ordinal),
            restoredPlacements.SelectMany(item => item.Records).Select(item => item.RecordId).Order(StringComparer.Ordinal));
        Assert.Null(new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root)).ReadPolicy(applied.TargetId));
        var afterRestore = await child.CreateDeviceAsync();
        Assert.Equal(child.AdapterLuid, afterRestore);
        Assert.Equal(1, actions.ApplyCount);
        Assert.Equal(savedHistory, File.ReadAllBytes(historyPath));
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
        await child.StopAsync();
        if (firstUse)
        {
            var evidenceRoot = GpuWindowLedgerTestData.NewRoot("native-first-use-" + api);
            var appliedLedgerPath = Path.Combine(evidenceRoot, "applied-ledger.json");
            var finalLedgerPath = Path.Combine(evidenceRoot, "restored-ledger.json");
            var retainedHistoryPath = Path.Combine(evidenceRoot, "software-history.json");
            File.WriteAllBytes(appliedLedgerPath, retainedLedger);
            File.Copy(ledgerPath, finalLedgerPath);
            File.Copy(historyPath, retainedHistoryPath);
            output.WriteLine("firstUsePublicCycle=" + JsonSerializer.Serialize(new
            {
                api, existingLayer, mixedCandidates, actualApi = expectedApi, firstUseCalls = actions.FirstUseCalls, observationWaits = actions.ObservationWaits,
                applyCalls = actions.ApplyCount, child.Identity, initialLuid = child.AdapterLuid, targetLuid = onTarget,
                restoredLuid = afterRestore, appliedLedgerPath, ledgerPath = finalLedgerPath, historyPath = retainedHistoryPath, remoteCalls,
                actualObservation = true, originalPublicRunOnce = !scheduledFirstUse, scheduledCycle = scheduledFirstUse,
                firstUsePolicySeeded = false,
                thirdPartyMigrationVerified = false, loadFixture = true
            }));
        }
        output.WriteLine($"fullAutomaticCycle=true; missingSnapshotNoEffect=true; savedApi={expectedApi}; actualInitialLuid={child.AdapterLuid}; actualTargetLuid={onTarget}; actualRestoredLuid={afterRestore}; policyRecovered=true; configurationCount={actions.ApplyCount}; loadFixture=true; thirdPartyMigrationVerified=false");
    }

    private static SchedulingProcessFactSnapshot CreateNativeGpuCycleFacts(
        GpuPlacementProcessInstance process, string softwareId, WindowsGpuAdapterInventoryRead inventory, int initialIndex)
    {
        const SchedulingProcessMetricMask mask = SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.RuntimeState;
        var now = inventory.ObservedAtUtcTicks;
        var datasets = CreateCurrentDatasetObservations(mask, 1, now).ToDictionary(item => item.Key, item => item.Value);
        datasets[SchedulingProcessMetricMask.GpuUsage] = datasets[SchedulingProcessMetricMask.GpuUsage] with
        {
            TopologyGeneration = inventory.Generation, TopologyFingerprint = inventory.TopologyFingerprint
        };
        return new(SamplingObservationStatus.Current, 1, now, 1, 1, 0, 0, mask, mask,
            [new(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath,
                softwareId, "Owned GPU cycle", "general", "General", 80, mask, 0, 0, 1,
                inventory.Adapters.Select(adapter => new SchedulingProcessGpuFact(adapter.Index,
                    NativePdhAdapterIdentity.Pack(adapter.Luid), SchedulingProcessMetricMask.GpuUsage,
                    adapter.Index == initialIndex ? 5 : 0, 0, 1, 0, inventory.Generation)).ToArray())],
            1, now, inventory.Generation, inventory.TopologyFingerprint)
        {
            DatasetObservations = datasets,
            SoftwareBaseScores = [new(softwareId, 80)]
        };
    }

    private static HardwareMetricSnapshot CreateNativeGpuCycleHardware(WindowsGpuAdapterInventoryRead inventory, ulong initialKey)
    {
        var sensors = new GpuSensorMetrics(new("fixture", "not-requested", null), null, null, null, null, null, null, null, null, null, null);
        return CreateHardwareSnapshot(memoryUsagePercent: 10, cpuUsagePercent: 10) with
        {
            Gpus = inventory.Adapters.Select(adapter => new GpuMetrics(adapter.Index, adapter.Name,
                NativePdhAdapterIdentity.Pack(adapter.Luid) == initialKey ? 5 : 0,
                0, 0, 0, 0, 0, adapter.DedicatedVideoMemoryBytes, 0, sensors)).ToArray(),
            GpuInventory = new(SamplingObservationStatus.Current, inventory.Generation, inventory.ObservedAtUtcTicks,
                inventory.ObservedCount, 0, 0, inventory.TopologyFingerprint,
                inventory.Adapters.Select(adapter => new SchedulingGpuAdapterObservation(adapter.Index,
                    NativePdhAdapterIdentity.Pack(adapter.Luid), SchedulingGpuCapabilityMask.Usage,
                    SchedulingGpuMetricMask.Usage, SamplingObservationStatus.Current, SamplingObservationStatus.Unsupported,
                    NativePdhAdapterIdentity.Pack(adapter.Luid) == initialKey ? 5 : 0, 0, 0,
                    inventory.Generation, inventory.ObservedAtUtcTicks)).ToArray())
        };
    }

    private sealed class CapturingGpuCycleActions(IRunningGpuPlacementActionService inner,
        IHostManagerRollbackStateStore ledger, D3d11ProxyShimRuntime runtime) : IRunningGpuPlacementActionService
    {
        internal int ApplyCount { get; private set; }
        internal RunningGpuPlacementActionPlan? Plan { get; private set; }
        internal List<Guid> CallIds { get; } = [];
        internal List<Guid> ObservationCallIds { get; } = [];
        internal int FirstUseCalls { get; private set; }
        internal int ObservationWaits { get; private set; }
        internal Func<GpuPlacementProcessInstance, Task>? BeforeObservationWait { get; set; }
        public Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
            => inner.PrepareAsync(request, token);
        public async Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            RunningGpuApiObservationExecution execution, CancellationToken token)
        {
            FirstUseCalls++;
            return await inner.PrepareWithFirstApiObservationAsync(request, execution with
            {
                ExecuteRemoteCallAsync = async (call, callToken) =>
                {
                    ObservationCallIds.Add(call.Request.CallId);
                    return await execution.ExecuteRemoteCallAsync(call, callToken);
                },
                WaitAsync = async (process, window, waitToken) =>
                {
                    ObservationWaits++;
                    if (BeforeObservationWait is not null) await BeforeObservationWait(process);
                    await execution.WaitAsync(process, window, waitToken);
                }
            }, token);
        }
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync, CancellationToken token)
            => inner.PrepareWithFirstApiObservationAsync(request, observeAsync, token);
        public async Task<RunningGpuPlacementActionResult> TryApplyAsync(RunningGpuPlacementActionPlan plan,
            RunningGpuPlacementExecution windows, CancellationToken token)
        {
            var placement = Assert.Single((await ledger.LoadAsync(token)).AppliedPlacements);
            var record = Assert.Single(placement.Records, record => record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
            Assert.True(GpuShimPolicyRecord.TryRead(record, out var policy));
            Assert.Equal(plan.PolicyValue, policy.AppliedValue);
            Assert.Equal(plan.PolicyValue, runtime.ReadPolicy(placement.TargetId));
            var unresolved = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata!["runtimeActionResult"])!;
            Assert.Equal(RunningGpuPlacementActionStatuses.Unresolved, unresolved.Status);
            ApplyCount++;
            Plan = plan;
            return await inner.TryApplyAsync(plan, windows with
            {
                ExecuteRemoteCallAsync = async (call, callToken) =>
                {
                    CallIds.Add(call.Request.CallId);
                    return await windows.ExecuteRemoteCallAsync(call, callToken);
                }
            }, token);
        }
    }
}
