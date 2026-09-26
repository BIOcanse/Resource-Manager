using System.Diagnostics;
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
    [Fact]
    public async Task FirstUseCleanupKeepsOneDeadlineAcrossBothStopsAndRetainsTheUnresolvedCall()
    {
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 789,
            [new(0, "Intel UHD Graphics", 0x8086, 1, 0, new AdapterLuid { LowPart = 123 }, false, 0, WindowsGpuAdapterKind.Integrated),
             new(1, "NVIDIA GeForce RTX 4090", 0x10de, 2, 0, new AdapterLuid { LowPart = 456 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)]);
        var process = AutomaticGpuProcess();
        var identity = new GpuPlacementProcessInstance(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath!);
        var facts = CreateNativeGpuCycleFacts(identity, process.SoftwareId, inventory, 1);
        var actions = new RecordingRunningGpuActions { Available = true, UnknownApi = true };
        var topology = CreateAutomaticPlacementTopology();
        var path = Path.Combine(GpuWindowLedgerTestData.NewRoot("first-use-cleanup-deadline"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1));
        await store.ReserveNativeHostSessionIncarnationAsync(default);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, runningGpuActions: actions, rollbackStateStoreOverride: store,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)))));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 456));
        var plan = fixture.RuntimePlan;
        fixture.RuntimePlanProvider.Publish(plan with
        {
            Version = plan.Version + 1,
            HostManager = plan.HostManager with { HotPublish = plan.HostManager.HotPublish with
            {
                PlacementCoordinator = plan.HostManager.HotPublish.PlacementCoordinator with
                {
                    WindowExecution = plan.HostManager.HotPublish.PlacementCoordinator.WindowExecution with { CleanupReserveMilliseconds = 2000 }
                }
            } },
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy> { [process.SoftwareId] = process.Policy },
                new Dictionary<string, ResolvedGpuPlacementPolicy>())
        });
        var created = new List<ControlledRemoteCall>();
        ControlledRemoteCall Make(GpuRemoteCallKind kind, bool complete)
        {
            var call = new ControlledRemoteCall(kind, complete,
                new(Guid.NewGuid(), identity, kind, checked((ulong)(4096 + created.Count * 256)), 16, kind == GpuRemoteCallKind.StopApiObservation));
            created.Add(call);
            return call;
        }
        TimeSpan elapsed = default;
        actions.FirstUse = async (_, execution, token) =>
        {
            Assert.True((await execution.ExecuteRemoteCallAsync(Make(GpuRemoteCallKind.StartApiObservation, true), token)).Completed);
            Assert.True((await execution.ExecuteRemoteCallAsync(Make(GpuRemoteCallKind.StartApiObservation, true), token)).Completed);
            var watch = Stopwatch.StartNew();
            Assert.True((await execution.ExecuteRemoteCallAsync(Make(GpuRemoteCallKind.StopApiObservation, true), execution.CleanupCancellationToken)).Completed);
            await Task.Delay(1000);
            var pending = Make(GpuRemoteCallKind.StopApiObservation, false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.ExecuteRemoteCallAsync(pending, execution.CleanupCancellationToken));
            elapsed = watch.Elapsed;
            Assert.True(execution.CleanupCancellationToken.IsCancellationRequested);
            var refused = Make(GpuRemoteCallKind.StopApiObservation, true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.ExecuteRemoteCallAsync(refused, execution.CleanupCancellationToken));
            Assert.Equal(0, refused.Starts);
            Assert.Equal(1, refused.Disposals);
            return new(null, "fixture cleanup timed out");
        };
        try
        {
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.RunOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.Equal("The original GPU action checkpoint has not been settled.", rejected.Message);
            await CurrentWindowTask(fixture.Coordinator).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            var records = Assert.Single((await store.LoadAsync(default)).AppliedPlacements).Records;
            var remote = records.Select(record => GpuRemoteCallRecord.TryRead(record, out var fact) ? fact : null).OfType<GpuRemoteCallRecord>().ToArray();
            Assert.Equal(4, remote.Length);
            Assert.Equal(4, remote.Select(fact => fact.Request.CallId).Distinct().Count());
            Assert.Equal(2, remote.Count(fact => fact.Request.Kind == GpuRemoteCallKind.StartApiObservation && fact.Result!.Completed));
            var unresolved = Assert.Single(remote, fact => fact.BlocksProcess);
            Assert.Equal(created[3].Request.CallId, unresolved.Request.CallId);
            Assert.NotNull(unresolved.Started!.ThreadId);
            Assert.Null(unresolved.Result!.ExitCode);
            Assert.Equal(0, actions.ApplyCalls);
            Assert.DoesNotContain(records, record => record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
            // Old code renews the timer at Stop B and takes about 3s; the shared reserve is 2s total.
            Assert.InRange(elapsed.TotalMilliseconds, 1500, 2700);
        }
        finally
        {
            foreach (var call in created) call.Complete = true;
            if (ReadPrivateField<object>(fixture.Coordinator, "gpuActionCheckpoint") is not null)
            {
                await CurrentWindowTask(fixture.Coordinator).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            }
            Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        }
    }
}
