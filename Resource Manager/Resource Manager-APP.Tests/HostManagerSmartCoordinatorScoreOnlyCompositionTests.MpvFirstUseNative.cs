using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [MpvFirstUseNativeFact]
    public Task FullFirstUsePublicMpvCycleIdentifiesAndMovesApplicationOwnedContextsThenRestores() =>
        RunPublicMpvCyclesAsync(1, TimeSpan.FromSeconds(45));

    [MpvFirstUseNativeFact]
    public Task RepeatedPublicMpvCyclesReuseFirstIdentificationAndRecordResourceLifetime() =>
        RunPublicMpvCyclesAsync(3, TimeSpan.FromSeconds(100));

    private async Task RunPublicMpvCyclesAsync(int cycleCount, TimeSpan timeout)
    {
        var root = GpuWindowLedgerTestData.NewRoot("mpv-first-use-native");
        output.WriteLine("mpvFirstUseRoot=" + root);
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_MPV_FIRST_USE_TARGET")!);
        var worker = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_WORKER")!);
        var adapter = ulong.Parse(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_ADAPTER")!, CultureInfo.InvariantCulture);
        var inventory = new WindowsGpuAdapterOrderReader().ReadInventory();
        using var resourceCapture = new GpuPlacementMpvResourceCapture(root, NativePdhAdapterIdentity.ComputeTopologyFingerprint(inventory.Adapters));
        await using var target = new GpuPlacementMpvTestProcess(executable, root, timeout);
        try
        {
            target.AfterPhaseSnapshot = stage => resourceCapture.CaptureAsync(stage, target.Identity);
            var originalRenderer = await target.ConnectAsync();
            var identity = target.Identity;
            Assert.Equal(SamplingObservationStatus.Current, inventory.Status);
            Assert.Equal(0U, inventory.SkippedCount);
            Assert.Equal(0U, inventory.OverflowCount);
            var initial = Assert.Single(inventory.Adapters, item => originalRenderer.Contains(item.Name, StringComparison.OrdinalIgnoreCase));
            var selected = Assert.Single(inventory.Adapters, item => NativePdhAdapterIdentity.Pack(item.Luid) == adapter);
            var source = NativePdhAdapterIdentity.Pack(initial.Luid);
            Assert.NotEqual(source, adapter);
            const string softwareId = "software:owned-mpv-first-use";
            const string displayName = "Owned mpv first use";
            var policy = ResolvedGpuPlacementPolicy.FromSoftware(GpuPlacementPolicyDefaults.CreateSoftwarePolicy(softwareId, displayName) with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto,
                SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
                RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
                TargetGpu = GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(selected.Name)
                    ? GpuPlacementTargets.IntegratedGpu : GpuPlacementTargets.HighPerformanceGpu
            });
            var facts = CreateNativeGpuCycleFacts(identity, softwareId, inventory, initial.Index);
            var ledgerPath = Path.Combine(root, "recovery.json");
            using var ledger = new JsonHostManagerRollbackStateStore(ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1));
            await ledger.ReserveNativeHostSessionIncarnationAsync(default);
            CapturingGpuCycleActions actions = null!;
            D3d11ProxyShimRuntime runtime = null!;
            var callback = new WindowsGpuCallbackPreparationRuntime(worker);
            var topology = CreateAutomaticPlacementTopology();
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true, scoreOnlyEnabled: false, processFactsSnapshot: facts, policyExecutionEnabled: false,
                automaticMemoryCleanupEnabled: false, optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
                cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
                automaticPlacementTopology: topology, rollbackStateStoreOverride: ledger, gpuCallbackRuntime: callback,
                processRecoveryRead: pid =>
                {
                    Assert.Equal(identity.ProcessId, pid);
                    return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid,
                        DateTimeOffset.FromFileTime(checked((long)identity.ProcessStartKey))));
                },
                runningGpuActionsFactory: (fixtureRoot, plans) =>
                {
                    runtime = new(new GpuPolicyEnvironment(fixtureRoot));
                    var history = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixtureRoot));
                    return actions = new(new WindowsRunningGpuPlacementActionService(
                        NullLogger<WindowsRunningGpuPlacementActionService>.Instance, runtime,
                        new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance), history, plans, callback), ledger, runtime);
                });
            fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, source));
            var identification = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixture.Root));
            Assert.Empty((await identification.GetSoftwareHistoryAsync(softwareId, displayName, default)).Processes);
            actions.BeforeObservationWait = async process =>
            {
                Assert.Equal(identity, process);
                Assert.Empty((await identification.GetSoftwareHistoryAsync(softwareId, displayName, default)).Processes);
                Assert.False(GpuActionFacts.HasPlacementEffects((await ledger.LoadAsync(default)).AppliedPlacements));
                Assert.Equal(originalRenderer, await target.RecreateAsync("observed-source"));
            };
            var enabled = fixture.RuntimePlan with
            {
                Version = fixture.RuntimePlan.Version + 1,
                GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy> { [softwareId] = policy },
                    new Dictionary<string, ResolvedGpuPlacementPolicy>())
            };
            fixture.RuntimePlanProvider.Publish(enabled);
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ObservationWaits);
            Assert.Equal(1, actions.ApplyCount);
            Assert.Equal(adapter, actions.Plan!.Request.TargetAdapterKey);
            Assert.Equal(GpuGraphicsApi.OpenGL, actions.Plan.GraphicsApis[identity.ProcessId]);
            Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single((await identification.GetSoftwareHistoryAsync(
                softwareId, displayName, default)).Processes).GraphicsApi);
            var historyPath = Path.Combine(fixture.Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
            var historyBytes = File.ReadAllBytes(historyPath);
            var applied = Assert.Single((await ledger.LoadAsync(default)).AppliedPlacements);
            var record = Assert.Single(applied.Records, item => item.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
            using var preparation = JsonDocument.Parse(record.Metadata!["openGlPreparation"]);
            var prepared = preparation.RootElement;
            Assert.Equal(adapter, prepared.GetProperty("CallbackAdapter").GetUInt64());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ExitCode").GetUInt32());
            Assert.False(prepared.GetProperty("Cleanup").GetProperty("TerminationRequested").GetBoolean());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ActiveProcessCount").GetUInt32());
            var summary = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata["runtimeActionResult"])!;
            Assert.Equal(RunningGpuPlacementActionStatuses.Prepared, summary.Status);
            Assert.False(summary.Applied);
            var processResult = Assert.Single(summary.Processes);
            Assert.True(processResult.Configured);
            Assert.Equal(identity, processResult.Identity);
            var calls = applied.Records.Where(GpuRemoteCallRecord.IsActionFact).Select(item =>
            {
                Assert.True(GpuRemoteCallRecord.TryRead(item, out var call));
                Assert.Equal(identity, call.Request.Process);
                Assert.True(call.Result!.Completed);
                Assert.False(call.BlocksProcess);
                return call;
            }).ToArray();
            Assert.Equal(6, calls.Length);
            Assert.Equal(new[] { GpuRemoteCallKind.LoadObservationProvider, GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StopApiObservation },
                actions.ObservationCallIds.Select(id => Assert.Single(calls, call => call.Request.CallId == id).Request.Kind));
            Assert.Equal(new[] { GpuRemoteCallKind.ConfigureProvider, GpuRemoteCallKind.ReadDevices, GpuRemoteCallKind.ReadDevices },
                actions.CallIds.Select(id => Assert.Single(calls, call => call.Request.CallId == id).Request.Kind));
            var appliedBytes = File.ReadAllBytes(ledgerPath);
            File.WriteAllBytes(Path.Combine(root, "applied-ledger.json"), appliedBytes);
            File.WriteAllBytes(Path.Combine(root, "software-history.json"), historyBytes);
            var movedRenderer = await target.RecreateAsync("configured-target");
            Assert.NotEqual(originalRenderer, movedRenderer);
            Assert.Contains(selected.Name, movedRenderer, StringComparison.OrdinalIgnoreCase);
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(appliedBytes, File.ReadAllBytes(ledgerPath));
            Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ApplyCount);
            fixture.RuntimePlanProvider.Publish(enabled with
            {
                Version = enabled.Version + 1,
                OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
            });
            _ = await fixture.Coordinator.RunOnceAsync(default);
            var restored = await ledger.LoadAsync(default);
            Assert.False(GpuActionFacts.HasPlacementEffects(restored.AppliedPlacements));
            Assert.False(GpuActionFacts.HasUnsettledActions(restored.AppliedPlacements));
            Assert.Null(runtime.ReadPolicy(applied.TargetId));
            File.Copy(ledgerPath, Path.Combine(root, "restored-ledger.json"));
            Assert.Equal(originalRenderer, await target.RecreateAsync("normal-source"));
            Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
            for (var cycle = 2; cycle <= cycleCount; cycle++)
            {
                fixture.RuntimePlanProvider.Publish(enabled with { Version = enabled.Version + (cycle - 1) * 2 });
                _ = await fixture.Coordinator.RunOnceAsync(default);
                Assert.Equal(1, actions.FirstUseCalls);
                Assert.Equal(1, actions.ObservationWaits);
                Assert.Equal(cycle, actions.ApplyCount);
                Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
                var cyclePlacement = Assert.Single((await ledger.LoadAsync(default)).AppliedPlacements);
                var cyclePolicy = Assert.Single(cyclePlacement.Records, item => item.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
                using var cyclePreparation = JsonDocument.Parse(cyclePolicy.Metadata!["openGlPreparation"]);
                var cleanup = cyclePreparation.RootElement.GetProperty("Cleanup");
                Assert.Equal(0U, cleanup.GetProperty("ExitCode").GetUInt32());
                Assert.False(cleanup.GetProperty("TerminationRequested").GetBoolean());
                Assert.Equal(0U, cleanup.GetProperty("ActiveProcessCount").GetUInt32());
                Assert.Equal(3 + 3 * cycle, cyclePlacement.Records.Count(GpuRemoteCallRecord.IsActionFact));
                File.Copy(ledgerPath, Path.Combine(root, $"applied-ledger-{cycle}.json"));
                Assert.Equal(movedRenderer, await target.RecreateAsync($"configured-target-{cycle}"));
                fixture.RuntimePlanProvider.Publish(enabled with
                {
                    Version = enabled.Version + (cycle - 1) * 2 + 1,
                    OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
                });
                _ = await fixture.Coordinator.RunOnceAsync(default);
                var cycleRestored = await ledger.LoadAsync(default);
                Assert.False(GpuActionFacts.HasPlacementEffects(cycleRestored.AppliedPlacements));
                Assert.False(GpuActionFacts.HasUnsettledActions(cycleRestored.AppliedPlacements));
                Assert.Null(runtime.ReadPolicy(applied.TargetId));
                File.Copy(ledgerPath, Path.Combine(root, $"restored-ledger-{cycle}.json"));
                Assert.Equal(originalRenderer, await target.RecreateAsync($"normal-source-{cycle}"));
                Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
            }
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
            await target.StopAsync();
            await resourceCapture.CaptureAsync("exited", identity, exited: true);
            await File.WriteAllTextAsync(Path.Combine(root, "product-result.json"), JsonSerializer.Serialize(new
            {
                passed = true, cycleCount, identity, sourceAdapter = source, targetAdapter = adapter, originalRenderer, movedRenderer,
                originalPublicRunOnce = true, firstUsePolicySeeded = false, firstUseCalls = actions.FirstUseCalls,
                observationWaits = actions.ObservationWaits, applyCalls = actions.ApplyCount,
                preparation = prepared.Clone(), summary, calls, inventory,
                thirdPartyTarget = true, controlledSchedulingInputs = true, visiblePresentationVerified = false,
                dedicatedGpuResourceSamplesRecorded = true, publishedResourceSubscription = false, sharedGpuMemoryMeasured = false,
                videoMemoryReleaseMeasured = false, performanceBenefitMeasured = false, deployed = false
            }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "primary-failure.txt"), error.ToString());
            throw;
        }
    }
}

internal sealed class MpvFirstUseNativeFactAttribute : FactAttribute
{
    public MpvFirstUseNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_MPV_FIRST_USE_TARGET")))
            Skip = "Requires the explicitly owned mpv target and retained current native images.";
    }
}
