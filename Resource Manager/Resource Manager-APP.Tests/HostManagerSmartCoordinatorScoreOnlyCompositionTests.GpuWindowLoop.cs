using System.Reflection;
using System.Text.Json;
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
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalGpuWindowLoopSubmitsOnlyTheExecutedPrefix(bool expireWindowWork)
    {
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 789,
            [new(0, "Intel UHD Graphics", 0x8086, 1, 0, new AdapterLuid { LowPart = 123 }, false, 0, WindowsGpuAdapterKind.Integrated),
             new(1, "NVIDIA GeForce RTX 4090", 0x10de, 2, 0, new AdapterLuid { LowPart = 456 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)]);
        var observations = new[] { "software:gpu-loop-first", "software:gpu-loop-tail" }.Select((softwareId, index) =>
            CreateNativeGpuCycleFacts(new(42412 + index, 133900000000000001,
                $"gpu-loop-{index}", $@"C:\fixture\gpu-loop-{index}.exe"), softwareId, inventory, 1)).ToArray();
        var facts = observations[0] with
        {
            EnumeratedCount = 2,
            EmittedCount = 2,
            Processes = observations.SelectMany(value => value.Processes).ToArray(),
            SoftwareBaseScores = [.. observations.SelectMany(value => value.SoftwareBaseScores)]
        };
        var topology = CreateAutomaticPlacementTopology();
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: true,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, runningGpuActions: actions,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime(133900000000000001))));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 456));
        var enabled = fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            GpuPlacement = new(true, observations.ToDictionary(value => value.Processes[0].SoftwareId!,
                _ => AutomaticGpuProcess().Policy), new Dictionary<string, ResolvedGpuPlacementPolicy>())
        };
        fixture.RuntimePlanProvider.Publish(enabled);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        var compute = fixture.Coordinator.SchedulingAuthority.Compute;
        Assert.NotNull(compute?.Gpu);
        enabled = enabled with
        {
            Version = enabled.Version + 1,
            Diagnostics = enabled.Diagnostics with { HostManagerSmartCoordinatorScoreOnlyEnabled = false }
        };
        fixture.RuntimePlanProvider.Publish(enabled);
        var coordinator = fixture.Coordinator;
        var capture = (Task)typeof(HostManagerSmartCoordinator).GetMethod("CaptureHostManagerSampleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, [enabled, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured);
        Assert.NotNull(sample);
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(newPointOfNoReturnCapacity: 2, recoveryCapacity: 1);
        using var lease = fixture.RuntimePlanProvider.AcquirePublicationLease();
        var desiredRuntime = new HostManagerSmartCoordinatorRuntime(fixture.DeploymentState).CaptureDesired(lease);
        WindowsRunningGpuPlacementActionService.WindowBatchResult? batch = null;
        var targets = new List<string>();
        actions.ApplyWithWindows = async (plan, windows, token) =>
        {
            targets.Add(plan.Request.TargetId);
            if (expireWindowWork)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                // The uncancelled batch token allows the actual owner callback to report its expired work.
                batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(
                    [GpuWindowLedgerTestData.Prepared().Window.Request], windows, CancellationToken.None);
                Assert.False(Assert.Single(batch.Results).CanContinue);
                Assert.True(batch.Stopped);
            }
            return new([], "fixture no device/window operation", RunningGpuPlacementActionStatuses.Skipped);
        };
        var running = (Task<bool>)typeof(HostManagerSmartCoordinator).GetMethod("RunAutomaticPlacementCycleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator,
                [admission, desiredRuntime, sample, compute, true, CancellationToken.None, null])!;
        Assert.Equal(!expireWindowWork, await running.WaitAsync(TimeSpan.FromSeconds(40)));
        Assert.Equal(2, actions.PrepareCalls);
        Assert.Equal(expireWindowWork ? 1 : 2, actions.ApplyCalls);
        var workspace = ReadPrivateField<NativePlacementCoordinatorWorkspace>(coordinator, "placementCoordinatorWorkspace")!;
        var session = ReadPrivateField<NativePlacementCoordinatorSession>(coordinator, "placementCoordinatorSession")!;
        var planned = workspace.Actions.Take(2).ToArray();
        Assert.All(planned, action =>
        {
            Assert.NotEqual(0UL, action.ActionId);
            Assert.Equal((uint)NativePlacementResourceKind.Gpu, action.ResourceKind);
            Assert.Equal((uint)NativePlacementActionDisposition.Apply, action.Disposition);
        });
        Assert.NotEqual(planned[0].TargetKey, planned[1].TargetKey);
        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.GetSnapshot(ref header, workspace.States));
        Assert.Equal(2U, header.ActiveStateCount);
        var states = workspace.States.Take(2).ToArray();
        for (var index = 0; index < 2; index++)
        {
            var action = planned[index];
            var applied = index < actions.ApplyCalls;
            var state = Assert.Single(states, row => row.TargetKey == action.TargetKey && row.RecordKey == action.RecordKey);
            Assert.Equal(applied ? NativePlacementSlotState.Applied : NativePlacementSlotState.PendingApply,
                (NativePlacementSlotState)state.State);
            Assert.Equal(applied ? 0UL : action.ActionId, state.PendingActionId);
            Assert.Equal(applied ? 0UL : action.DeadlineMilliseconds, state.RetryAtMilliseconds);
        }
        var receipts = fixture.StateStore.Current.AppliedPlacements;
        Assert.Equal(actions.ApplyCalls, receipts.Count);
        Assert.All(receipts, receipt => Assert.Equal(HostManagerAppliedRecordKinds.GpuShimPolicy, Assert.Single(receipt.Records).Kind));
        var firstReceipt = Assert.Single(receipts, receipt => receipt.TargetId == targets[0]);
        var firstIdentity = HostManagerPlacementCoordinatorProjection.CreateIdentity(firstReceipt, Assert.Single(firstReceipt.Records));
        Assert.Equal(firstIdentity.TargetKey, planned[0].TargetKey);
        Assert.Equal(firstIdentity.RecordKey, planned[0].RecordKey);
        Assert.Equal((uint)NativePlacementFeedbackStatus.Applied, workspace.Feedback[0].Status);
        Assert.Equal(planned[0].ActionId, workspace.Feedback[0].ActionId);
        Assert.True(workspace.Feedback[0].CompletedAtMilliseconds < planned[0].DeadlineMilliseconds);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
        output.WriteLine("originalGpuWindowLoop=" + JsonSerializer.Serialize(new
        {
            expireWindowWork, actions.PrepareCalls, actions.ApplyCalls, planned, states, batch,
            receipts, targets, feedback = workspace.Feedback.Take(actions.ApplyCalls).ToArray(),
            actualNativeSession = true, originalManagedLoop = true, windowOperationStarted = false
        }, new JsonSerializerOptions { IncludeFields = true }));
    }
}
