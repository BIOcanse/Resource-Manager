using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;
using static Resource_Manager_APP.Tests.HostManagerSmartCoordinatorScoreOnlyCompositionTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests
{
    [WindowExecutorTheory]
    [InlineData("normal")]
    [InlineData("pending-save")]
    [InlineData("changed-timeout")]
    [InlineData("target-exit")]
    public async Task WindowProductOwnerUsesOneDeadlineAndRetainsActualFactsThroughTheServiceBatch(string mode)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        var before = await ReadOwnedWindow(window);
        var path = Path.Combine(NewRoot("product-owner"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        // Enumeration/provider are fixture inputs; batch, owner, helper, window and store are production paths.
        var requests = mode == "normal" ? new[] { request with { Window = 1 }, request, request with { Window = 1 } }
            : new[] { request, request with { Window = 1 } };
        var actions = new ProductWindowBatchFixture(requests);
        await WithWindowLedgerCoordinatorAsync(store, async coordinator =>
        {
            var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with
            { AppliedPlacements = [Placement(Policy(Prepared() with { Window = new(request, window.NativeThreadId, before) }))] };
            await store.SaveAsync(state, default);
            var placement = state.AppliedPlacements[0];
            var desired = new HostManagerPlacementDesired(placement, placement.Records[0], 30)
            {
                RuntimeGpuAction = new(new(placement.TargetId, placement.SoftwareId, placement.DisplayName,
                    [new(request.ProcessId, (ulong)request.CreationFileTimeUtc, "private-window", window.Identity.ExecutablePath)],
                    "fixture", 123, "window-rerender"), [2],
                    new Dictionary<int, GpuGraphicsApi> { [request.ProcessId] = GpuGraphicsApi.D3D11 })
            };
            var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], new NativePlacementDesiredInput[1]).Records.Single();
            if (mode == "pending-save") committer.PauseNext();
            if (mode is "changed-timeout" or "target-exit") window.BlockNextResize(0x47);
            var deadline = checked((ulong)Environment.TickCount64 + 5000);
            var running = (Task<(HostManagerRollbackStateDocument State, bool CanContinue)>)typeof(HostManagerSmartCoordinator)
                .GetMethod("ExecuteGpuShimActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, [state, projected, deadline, CancellationToken.None])!;
            try
            {
                if (mode == "pending-save") await committer.Entered.WaitAsync(TimeSpan.FromSeconds(4));
                if (mode is "changed-timeout" or "target-exit") Assert.True(window.ChangingEntered.Wait(TimeSpan.FromSeconds(4)));
                var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(mode == "normal", result.CanContinue);
                Assert.NotNull(actions.Batch);
                Assert.Equal(mode == "normal" ? 3 : 1, actions.Batch.Results.Count);
                Assert.Equal(mode != "normal", actions.Batch.Stopped);
                if (mode == "pending-save")
                {
                    Assert.False(await SettleWindowCheckpoint(coordinator));
                    Assert.Equal(before, await ReadOwnedWindow(window));
                    Assert.Empty(window.Requests);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.GetStateAsync(default));
                }
                committer.Release();
                await (Task)typeof(HostManagerSmartCoordinator).GetMethod("DrainGpuActionCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(coordinator, null)!;
                await (Task)typeof(HostManagerSmartCoordinator).GetMethod("DrainGpuWindowExecutionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(coordinator, null)!;
                var saved = ReadCanonical(path);
                var fact = Assert.Single(saved.AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
                Assert.True(GpuWindowActionRecord.TryRead(fact, out var decoded));
                var actual = Assert.IsType<GpuWindowActionResult>(decoded.Result);
                Assert.Equal(mode == "normal" ? GpuWindowActionOutcome.WindowRestored
                    : mode == "pending-save" ? GpuWindowActionOutcome.NotExecuted : GpuWindowActionOutcome.Unresolved, actual.Outcome);
                Assert.True(actual.Cleanup.Complete);
                Assert.Equal(mode == "normal" ? 0U : 995U, actual.Cleanup.ExitCode);
                Assert.Equal(mode != "normal", actual.Cleanup.TerminationRequested);
                Assert.Equal(mode == "pending-save", actual.PersistencePending);
                Assert.Equal(mode != "pending-save", actual.AuthorizationMayHaveBeenSent);
                var policy = Assert.Single(saved.AppliedPlacements[0].Records, record => record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
                var summary = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(policy.Metadata!["runtimeActionResult"])!;
                Assert.Equal(actions.Batch.Results.Count(result => result.Record is not null), summary.Records.Count);
                Assert.Equal(mode != "normal", GpuWindowActionRecord.BlocksProcess(saved.AppliedPlacements,
                    "retargeted", request.ProcessId, (ulong)request.CreationFileTimeUtc));
                window.AllowResize();
                var after = await ReadOwnedWindow(window);
                if (mode is "changed-timeout" or "target-exit")
                {
                    Assert.Equal(81, after.Width);
                    Assert.Single(window.Requests);
                    if (mode == "target-exit")
                    {
                        ClosePrivateWindow(window, input);
                        var admission = HostManagerCycleEffectAdmission.Create(false);
                        admission.InitializeBudgets(1, 1);
                        Assert.True(await ReconcileWindowExit(coordinator, admission));
                        var retained = Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
                        Assert.True(GpuWindowActionRecord.TryRead(retained, out var retired));
                        Assert.Equal(actual, retired.Result);
                        Assert.Equal(decoded.Prepared, retired.Prepared);
                        Assert.NotNull(retired.ExitSettlement);
                        Assert.False(retired.BlocksAutomaticAction);
                        Assert.False(GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
                        output.WriteLine("nativeWindowTargetExit=" + JsonSerializer.Serialize(new
                        { path, beforeExitWidth = after.Width, factBeforeExit = decoded, factAfterExit = retired,
                            canonicalReadBeforeDispose = true, windowRestoreRetried = false }));
                    }
                    else
                    {
                        await ResizeOwnedWindow(window, 80);
                        output.WriteLine("explicitTestRecovery=true; productResultRemainsUnresolved=true");
                    }
                }
                else
                {
                    Assert.Equal(before, after);
                    Assert.Equal(mode == "normal" ? 2 : 0, window.Requests.Length);
                }
                output.WriteLine("nativeWindowProductOwner=" + JsonSerializer.Serialize(new
                { mode, path, deadline, result.CanContinue, actions.Batch, fact, summary, canonicalReadBeforeDispose = true }));
            }
            finally
            {
                committer.Release();
                window.AllowResize();
                await running.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }, new WindowsGpuWindowActionRuntime(WindowWorkerPath, window.DesktopName), actions,
            mode == "target-exit" ? new WindowsProcessResourcePolicyWriter(
                new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256)).ReadProcessInstanceForRecovery : null);
        ClosePrivateWindow(window, input);
    }

    private sealed class ProductWindowBatchFixture(GpuWindowActionRequest[] requests) : IRunningGpuPlacementActionService
    {
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            RunningGpuApiObservationExecution execution, CancellationToken token)
            => throw new InvalidOperationException("This fixture starts after the existing policy preparation.");
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync, CancellationToken token)
            => throw new InvalidOperationException("This fixture starts after the existing policy preparation.");
        internal WindowsRunningGpuPlacementActionService.WindowBatchResult? Batch;
        public Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
            => throw new InvalidOperationException("This fixture starts after the existing policy preparation.");
        public async Task<RunningGpuPlacementActionResult> TryApplyAsync(RunningGpuPlacementActionPlan plan,
            RunningGpuPlacementExecution windows, CancellationToken token)
        {
            Batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(requests, windows, token);
            return new(Batch.Results.Where(result => result.Record is not null).Select(result => result.Record!).ToArray(),
                "fixture provider; actual window results", RunningGpuPlacementActionStatuses.Unresolved);
        }
    }
}
