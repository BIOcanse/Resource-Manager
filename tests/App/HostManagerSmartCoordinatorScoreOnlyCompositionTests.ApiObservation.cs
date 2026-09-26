using System.Reflection;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;
using static Resource_Manager_APP.Tests.GpuWindowActionExitRecordTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(GpuRemoteCallKind.LoadObservationProvider)]
    [InlineData(GpuRemoteCallKind.StartApiObservation)]
    [InlineData(GpuRemoteCallKind.ReadApiObservation)]
    [InlineData(GpuRemoteCallKind.StopApiObservation)]
    public async Task ApiObservationUsesOriginalDurableCallWithoutCreatingASelectionPolicy(GpuRemoteCallKind kind)
    {
        var path = Path.Combine(NewRoot("api-observation"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(kind, completed: true);
        var process = ObservationProcess(call);
        call.BeforeStart = () =>
        {
            var placement = Assert.Single(ReadCanonical(path).AppliedPlacements);
            Assert.Equal(process.SoftwareId, placement.SoftwareId);
            Assert.Single(placement.Records);
            Assert.Equal(kind, ReadRemote(path).Request.Kind);
            Assert.True(ReadRemote(path).BlocksProcess);
        };
        var returned = await InvokeObservationOwner(fixture.Coordinator, await store.LoadAsync(default), process, call);
        Assert.True(returned.Result.Completed);
        Assert.Equal(1, call.Starts);
        Assert.Equal(1, call.Disposals);
        var saved = ReadCanonical(path).AppliedPlacements;
        Assert.Single(Assert.Single(saved).Records);
        Assert.False(ReadRemote(path).BlocksProcess);
        Assert.False(GpuActionFacts.HasPlacementEffects(saved));
        Assert.Empty(GpuActionFacts.PlacementEffects(saved));
        Assert.False(GpuActionFacts.HasUnsettledActions(saved));
    }

    [Theory]
    [InlineData(GpuRemoteCallKind.LoadProvider)]
    [InlineData(GpuRemoteCallKind.ConfigureProvider)]
    [InlineData(GpuRemoteCallKind.ReadDevices)]
    [InlineData((GpuRemoteCallKind)99)]
    public async Task ApiObservationPermissionCannotExecuteSelectionCalls(GpuRemoteCallKind kind)
    {
        var path = Path.Combine(NewRoot("api-no-selection"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(kind, completed: true);
        var state = await store.LoadAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeObservationOwner(fixture.Coordinator,
            state, ObservationProcess(call), call));
        Assert.Equal(0, call.Starts);
        Assert.Equal(1, call.Disposals);
        Assert.Empty((await store.LoadAsync(default)).AppliedPlacements);
    }

    [Theory]
    [InlineData(GpuRemoteCallKind.LoadObservationProvider)]
    [InlineData(GpuRemoteCallKind.StartApiObservation)]
    [InlineData(GpuRemoteCallKind.ReadApiObservation)]
    [InlineData(GpuRemoteCallKind.StopApiObservation)]
    public async Task SelectionOwnerCannotSilentlyConvertItsPolicyIntoObservationPermission(GpuRemoteCallKind kind)
    {
        var path = Path.Combine(NewRoot("selection-no-observation"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var state = await SeedRemotePolicy(store);
        var call = new ControlledRemoteCall(kind, completed: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRemoteOwner(fixture.Coordinator, state, call, default));
        Assert.Equal(0, call.Starts);
        Assert.Equal(1, call.Disposals);
        Assert.DoesNotContain(ReadCanonical(path).AppliedPlacements.SelectMany(item => item.Records), GpuRemoteCallRecord.IsActionFact);
    }

    [Theory]
    [InlineData("global-disabled")]
    [InlineData("not-actionable")]
    [InlineData("disabled")]
    [InlineData("preview")]
    [InlineData("ordinary")]
    [InlineData("no-shim")]
    [InlineData("no-software")]
    [InlineData("no-path")]
    [InlineData("relative-path")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-birth")]
    [InlineData("wrong-image")]
    [InlineData("wrong-ledger")]
    [InlineData("wrong-software")]
    public async Task ApiObservationRejectsUnpermittedOrMismatchedInputsBeforeAnyCall(string mutation)
    {
        var path = Path.Combine(NewRoot("api-rejection"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.StartApiObservation, completed: true);
        var process = ObservationProcess(call);
        var key = HostManagerPlacementReceiptKey.Create(OptimizationResourceKinds.Gpu, process.TargetId);
        var state = await store.LoadAsync(default);
        switch (mutation)
        {
            case "global-disabled": fixture.RuntimePlanProvider.Publish(fixture.RuntimePlanProvider.Current with
                { Version = fixture.RuntimePlanProvider.Current.Version + 1,
                    GpuPlacement = fixture.RuntimePlanProvider.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false } }); break;
            case "not-actionable": process = process with { CanApplyPhysicalPlacement = false }; break;
            case "disabled": process = process with { Policy = process.Policy with { EnabledMode = GpuPlacementPolicyModes.Disabled } }; break;
            case "preview": process = process with { Policy = process.Policy with { EnabledMode = GpuPlacementPolicyModes.Preview } }; break;
            case "ordinary": process = process with { Policy = process.Policy with { SchedulingMode = GpuPlacementSchedulingModes.Ordinary } }; break;
            case "no-shim": process = process with { Policy = process.Policy with { AllowedProviders = [] } }; break;
            case "no-software": process = process with { SoftwareId = "" }; break;
            case "no-path": process = process with { ExecutablePath = null }; break;
            case "relative-path": process = process with { ExecutablePath = "fixture.exe" }; break;
            case "wrong-pid": process = process with { ProcessId = process.ProcessId + 1 }; break;
            case "wrong-birth": process = process with { ProcessStartKey = process.ProcessStartKey + 1 }; break;
            case "wrong-image": process = process with { ExecutablePath = "C:\\other.exe" }; break;
            case "wrong-ledger": key = HostManagerPlacementReceiptKey.Create(OptimizationResourceKinds.Cpu, process.TargetId); break;
            case "wrong-software": state = state with { AppliedPlacements = [new(process.TargetId, process.DisplayName,
                "software:other", OptimizationResourceKinds.Gpu, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)] }; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeObservationOwner(fixture.Coordinator, state, process, call, key: key));
        Assert.Equal(0, call.Starts);
        Assert.Equal(1, call.Disposals);
        Assert.Empty((await store.LoadAsync(default)).AppliedPlacements);
    }

    [Fact]
    public async Task ApiObservationCancellationAndLateCompletionUseTheOriginalCheckpointAndSingleFact()
    {
        var path = Path.Combine(NewRoot("api-observation-late"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.ReadApiObservation);
        var process = ObservationProcess(call);
        using var stop = new CancellationTokenSource();
        var running = InvokeObservationOwner(fixture.Coordinator, await store.LoadAsync(default), process, call, stop.Token);
        await call.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checkpoint = CurrentWindowTask(fixture.Coordinator);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
        var original = ReadRemote(path);
        Assert.Null(original.Result!.ExitCode);
        Assert.True(GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
        var next = new ControlledRemoteCall(GpuRemoteCallKind.StopApiObservation, completed: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeObservationOwner(fixture.Coordinator,
            ReadCanonical(path), process, next));
        Assert.Equal(0, next.Starts);
        Assert.Equal(1, next.Disposals);
        call.Complete = true;
        Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        var settled = ReadRemote(path);
        Assert.Equal(original.RecordId, settled.RecordId);
        Assert.Equal(original.Result, settled.Result);
        Assert.True(settled.Settlement!.Completed);
        Assert.False(settled.BlocksProcess);
        Assert.Equal(1, call.Starts);
        Assert.Equal(1, call.Disposals);
        Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records);
    }

    private static HostManagerAutomaticPlacementProcess ObservationProcess(GpuRemoteCallExecution call)
    {
        var target = call.Request.Process;
        var process = AutomaticGpuProcess();
        return process with { ProcessId = target.ProcessId, ProcessStartKey = target.ProcessStartKey,
            ProcessName = target.ProcessName, ExecutablePath = target.ExecutablePath,
            Policy = process.Policy with { RuntimeHotSwitchEnabled = false, TargetGpu = GpuPlacementTargets.SystemDefaultGpu } };
    }

    [Theory]
    [InlineData(GpuRemoteCallKind.LoadObservationProvider, false)]
    [InlineData(GpuRemoteCallKind.StartApiObservation, false)]
    [InlineData(GpuRemoteCallKind.ReadApiObservation, false)]
    [InlineData(GpuRemoteCallKind.StopApiObservation, true)]
    [InlineData(GpuRemoteCallKind.ConfigureProvider, false)]
    public async Task ApiObservationRevocationAllowsOnlyStoppingTheAlreadyOwnedObservation(GpuRemoteCallKind nextKind, bool allowed)
    {
        var path = Path.Combine(NewRoot("api-observation-revoke"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var start = new ControlledRemoteCall(GpuRemoteCallKind.StartApiObservation, completed: true);
        var process = ObservationProcess(start);
        var started = await InvokeObservationOwner(fixture.Coordinator, await store.LoadAsync(default), process, start);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlanProvider.Current with
        {
            Version = fixture.RuntimePlanProvider.Current.Version + 1,
            GpuPlacement = fixture.RuntimePlanProvider.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false }
        });
        process = process with { CanApplyPhysicalPlacement = false, Policy = process.Policy with
            { EnabledMode = GpuPlacementPolicyModes.Disabled, AllowedProviders = [] } };
        var next = new ControlledRemoteCall(nextKind, completed: true);
        if (allowed)
        {
            var stopped = await InvokeObservationOwner(fixture.Coordinator, started.State, process, next);
            Assert.True(stopped.Result.Completed);
            Assert.Equal(1, next.Starts);
            Assert.Equal(2, Assert.Single(ReadCanonical(path).AppliedPlacements).Records.Count);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeObservationOwner(fixture.Coordinator, started.State, process, next));
            Assert.Equal(0, next.Starts);
            Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records);
        }
        Assert.Equal(1, next.Disposals);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-started")]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("image")]
    public async Task ApiObservationRevokedStopRequiresItsActualOriginalStart(string mutation)
    {
        var path = Path.Combine(NewRoot("api-stop-no-owner"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var start = new ControlledRemoteCall(GpuRemoteCallKind.StartApiObservation, completed: true);
        var process = ObservationProcess(start);
        var returned = await InvokeObservationOwner(fixture.Coordinator, await store.LoadAsync(default), process, start);
        var original = ReadRemote(path);
        var altered = mutation switch
        {
            "not-started" => original with { Started = null, Result = new(0, null, null, null, null, true, "not-started", null) },
            "pid" => original with { Request = original.Request with { Process = original.Request.Process with { ProcessId = process.ProcessId + 1 } } },
            "birth" => original with { Request = original.Request with { Process = original.Request.Process with { ProcessStartKey = process.ProcessStartKey + 1 } } },
            "image" => original with { Request = original.Request with { Process = original.Request.Process with { ExecutablePath = "C:\\other.exe" } } },
            _ => original
        };
        var placement = returned.State.AppliedPlacements[0];
        var state = returned.State with { AppliedPlacements = [placement with { Records = mutation == "missing" ? [] : [altered.Encode()] }] };
        process = process with { Policy = process.Policy with { AllowedProviders = [] } };
        var stop = new ControlledRemoteCall(GpuRemoteCallKind.StopApiObservation, completed: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeObservationOwner(fixture.Coordinator, state, process, stop));
        Assert.Equal(0, stop.Starts);
        Assert.Equal(1, stop.Disposals);
        Assert.Equal(original.Encode().Metadata!["remoteCallPayload"], ReadRemote(path).Encode().Metadata!["remoteCallPayload"]);
    }

    [Theory]
    [InlineData(GpuRemoteCallKind.LoadObservationProvider)]
    [InlineData(GpuRemoteCallKind.StartApiObservation)]
    public async Task ApiObservationRevokedWhileIntentCommitIsPausedDoesNotStart(GpuRemoteCallKind kind)
    {
        var path = Path.Combine(NewRoot("api-revoked-before-start"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(kind, completed: true);
        var state = await store.LoadAsync(default);
        committer.PauseNext();
        var running = InvokeObservationOwner(fixture.Coordinator, state, ObservationProcess(call), call);
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlanProvider.Current with
            {
                Version = fixture.RuntimePlanProvider.Current.Version + 1,
                GpuPlacement = fixture.RuntimePlanProvider.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false }
            });
            committer.Release();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, call.Starts);
            Assert.Equal(1, call.Disposals);
            Assert.Equal("not-started", result.Result.Status);
            Assert.Null(result.Result.ExitCode);
            Assert.False(ReadRemote(path).BlocksProcess);
            Assert.False(GpuActionFacts.HasPlacementEffects(ReadCanonical(path).AppliedPlacements));
        }
        finally { committer.Release(); }
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("id")]
    [InlineData("length")]
    public async Task ApiObservationMalformedIntentDoesNotRetainAnUnstartedOwner(string mutation)
    {
        var path = Path.Combine(NewRoot("api-invalid-intent"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var valid = new ControlledRemoteCall(GpuRemoteCallKind.LoadObservationProvider, completed: true);
        var request = mutation switch
        {
            "entry" => valid.Request with { FunctionAddress = 0 },
            "id" => valid.Request with { CallId = Guid.Empty },
            _ => valid.Request with { ParameterByteLength = 0 }
        };
        var invalid = new ControlledRemoteCall(GpuRemoteCallKind.LoadObservationProvider, completed: true, request: request);
        var state = await store.LoadAsync(default);
        var process = ObservationProcess(valid);
        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeObservationOwner(fixture.Coordinator, state, process, invalid));
        Assert.Equal(0, invalid.Starts);
        Assert.Equal(1, invalid.Disposals);
        Assert.Empty((await store.LoadAsync(default)).AppliedPlacements);
        var result = await InvokeObservationOwner(fixture.Coordinator, state, process, valid);
        Assert.True(result.Result.Completed);
        Assert.Equal(1, valid.Starts);
        Assert.Equal(1, valid.Disposals);
    }

    [Fact]
    public async Task ApiObservationCancelledBeforeItsFirstDurableCommitNeverStarts()
    {
        var path = Path.Combine(NewRoot("api-observation-before-start"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.LoadObservationProvider);
        var state = await store.LoadAsync(default);
        using var stop = new CancellationTokenSource();
        committer.PauseNext();
        var running = InvokeObservationOwner(fixture.Coordinator, state, ObservationProcess(call), call, stop.Token);
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var checkpoint = CurrentWindowTask(fixture.Coordinator);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(0, call.Starts);
            committer.Release();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            Assert.Equal(0, call.Starts);
            Assert.Equal("not-started", ReadRemote(path).Result!.Status);
            Assert.False(ReadRemote(path).BlocksProcess);
            Assert.False(GpuActionFacts.HasPlacementEffects(ReadCanonical(path).AppliedPlacements));
        }
        finally { committer.Release(); }
    }

    [Fact]
    public async Task ApiObservationUnknownCallSurvivesOwnerRestartWithoutPolicyOrReplay()
    {
        var path = Path.Combine(NewRoot("api-observation-restart"), "recovery.json");
        var call = new ControlledRemoteCall(GpuRemoteCallKind.StartApiObservation);
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
        {
            await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
            using var stop = new CancellationTokenSource();
            var running = InvokeObservationOwner(fixture.Coordinator, await store.LoadAsync(default), ObservationProcess(call), call, stop.Token);
            await call.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var checkpoint = CurrentWindowTask(fixture.Coordinator);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
        }
        Assert.Equal(1, call.Disposals);
        var original = ReadRemote(path);
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var exited = false;
        await using var restarted = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: reopened, automaticMemoryCleanupEnabled: false,
            processRecoveryRead: pid => exited ? Gone() : Live(pid, checked((long)call.Request.Process.ProcessStartKey)));
        Assert.True(await ReconcileRemoteOwner(restarted.Coordinator));
        Assert.True(ReadRemote(path).BlocksProcess);
        exited = true;
        Assert.True(await ReconcileRemoteOwner(restarted.Coordinator));
        var settled = ReadRemote(path);
        Assert.Equal(original.Result, settled.Result);
        Assert.NotNull(settled.ExitSettlement);
        Assert.False(settled.BlocksProcess);
        Assert.Null(settled.Result!.ExitCode);
        Assert.Equal(1, call.Starts);
    }

    private static Task<(HostManagerRollbackStateDocument State, GpuRemoteCallSnapshot Result)> InvokeObservationOwner(
        HostManagerSmartCoordinator coordinator, HostManagerRollbackStateDocument state,
        HostManagerAutomaticPlacementProcess process, GpuRemoteCallExecution call, CancellationToken token = default,
        HostManagerPlacementReceiptKey? key = null)
        => (Task<(HostManagerRollbackStateDocument, GpuRemoteCallSnapshot)>)typeof(HostManagerSmartCoordinator)
            .GetMethod("ExecuteOwnedGpuApiObservationCallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [state, key ?? HostManagerPlacementReceiptKey.Create(OptimizationResourceKinds.Gpu, process.TargetId),
                process, call, token])!;
}
