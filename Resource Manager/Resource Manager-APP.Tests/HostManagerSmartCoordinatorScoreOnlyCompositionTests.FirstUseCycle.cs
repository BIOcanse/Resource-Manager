using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("empty")]
    [InlineData("revoke")]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("software-during-save")]
    [InlineData("process-during-save")]
    [InlineData("closing-during-save")]
    public async Task FirstUsePublicCycleWaitsWithoutTheGateAndContinuesOnlyItsOriginalPlacement(string outcome)
    {
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 789,
            [new(0, "Intel UHD Graphics", 0x8086, 1, 0, new AdapterLuid { LowPart = 123 }, false, 0, WindowsGpuAdapterKind.Integrated),
             new(1, "NVIDIA GeForce RTX 4090", 0x10de, 2, 0, new AdapterLuid { LowPart = 456 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)]);
        var process = AutomaticGpuProcess();
        var facts = CreateNativeGpuCycleFacts(new(process.ProcessId, process.ProcessStartKey,
            process.ProcessName, process.ExecutablePath!), process.SoftwareId, inventory, 1);
        var actions = new RecordingRunningGpuActions { Available = true, UnknownApi = true };
        var topology = CreateAutomaticPlacementTopology();
        var duringSave = outcome.EndsWith("-during-save", StringComparison.Ordinal);
        var ledgerPath = Path.Combine(GpuWindowLedgerTestData.NewRoot("first-use-paused-save"), "recovery.json");
        using var committer = duringSave ? new PausedWindowCommitter() : null;
        using var store = committer is null ? null : new JsonHostManagerRollbackStateStore(ledgerPath,
            TimeProvider.System, TimeSpan.FromMinutes(1), committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        if (store is not null) await store.ReserveNativeHostSessionIncarnationAsync(default);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, runningGpuActions: actions,
            rollbackStateStoreOverride: store,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)))));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 456));
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { [process.SoftwareId] = process.Policy }, new Dictionary<string, ResolvedGpuPlacementPolicy>())
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        actions.FirstUse = async (request, execution, token) =>
        {
            calls++;
            Assert.Equal(process.ProcessId, Assert.Single(request.Processes).ProcessId);
            Assert.Equal(fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator.ApiObservationWindowMilliseconds,
                execution.DurationMilliseconds);
            if (committer is not null)
            {
                var call = new ControlledRemoteCall(GpuRemoteCallKind.LoadObservationProvider, completed: true,
                    request: new(Guid.NewGuid(), Assert.Single(request.Processes),
                        GpuRemoteCallKind.LoadObservationProvider, 4096, 80, false));
                committer.PauseNext();
                var remote = execution.ExecuteRemoteCallAsync(call, token);
                await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                entered.SetResult();
                var result = await remote;
                Assert.Equal(0, call.Starts);
                Assert.Equal(1, call.Disposals);
                Assert.Equal("not-started", result.Status);
                Assert.Null(result.ExitCode);
                Assert.False(ReadRemote(ledgerPath).BlocksProcess);
                return new(null, "fixture start refused after save");
            }
            entered.SetResult();
            await release.Task.WaitAsync(token);
            actions.UnknownApi = false;
            return outcome == "empty" ? new(null, "fixture no actual API")
                : new(new(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey),
                    request.Processes.ToDictionary(item => item.ProcessId, _ => GpuGraphicsApi.D3D11)), "fixture actual API");
        };
        using var cancel = new CancellationTokenSource();
        var run = fixture.Coordinator.RunOnceAsync(cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
            if (!duringSave)
            {
                Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
                gate.Release();
            }
            var sequence = fixture.Workspace.Snapshot.CycleSequence;
            Assert.Equal(0, actions.ApplyCalls);
            Task? close = null;
            if (outcome == "revoke") fixture.RuntimePlanProvider.Publish(fixture.RuntimePlanProvider.Current with
                { Version = fixture.RuntimePlanProvider.Current.Version + 1,
                    GpuPlacement = fixture.RuntimePlanProvider.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false } });
            if (outcome == "cancel") cancel.Cancel();
            if (outcome is "software-during-save" or "process-during-save")
            {
                var current = fixture.RuntimePlanProvider.Current;
                var denied = process.Policy with { AllowedProviders = [] };
                var plan = outcome == "software-during-save"
                    ? current.GpuPlacement with { SoftwarePoliciesBySoftwareId = new Dictionary<string, ResolvedGpuPlacementPolicy> { [process.SoftwareId] = denied } }
                    : current.GpuPlacement with { ProcessPoliciesBySoftwareAndProcessKey = new Dictionary<string, ResolvedGpuPlacementPolicy>
                    { [CompiledBaseScorePlan.CreateProcessPolicyKey(process.SoftwareId,
                        JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath))] = denied } };
                fixture.RuntimePlanProvider.Publish(current with { Version = current.Version + 1, GpuPlacement = plan });
            }
            if (outcome is "close" or "closing-during-save")
            {
                close = fixture.Coordinator.StopAsync(default);
                Assert.False(close.IsCompleted);
            }
            release.TrySetResult();
            committer?.Release();
            if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, calls);
            Assert.Equal(outcome == "complete" ? 1 : 0, actions.ApplyCalls);
            if (close is not null) await close.WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                Assert.Equal(sequence, fixture.Workspace.Snapshot.CycleSequence);
                Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
                gate.Release();
            }
            if (outcome == "complete")
            {
                Assert.Contains(fixture.StateStore.Current.AppliedPlacements.SelectMany(item => item.Records),
                    item => item.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
                await fixture.Coordinator.RunOnceAsync(default);
                Assert.Equal(1, calls);
                Assert.Equal(1, actions.ApplyCalls);
            }
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
        }
        finally
        {
            committer?.Release();
            release.TrySetResult();
            cancel.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }
}
