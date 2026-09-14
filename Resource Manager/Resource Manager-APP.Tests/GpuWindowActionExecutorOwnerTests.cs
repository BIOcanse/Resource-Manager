using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowOwnerActualExecutorRetainsTheOriginalTaskAndNeverAuthorizesAfterTimeout(bool delayedCommit)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        var original = await ReadOwnedWindow(window);
        var path = Path.Combine(NewRoot("native-original-owner"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
                   committer, WindowsHostManagerDurableRootManifestCommitter.Instance))
        {
            await WithWindowLedgerCoordinatorAsync(store, async coordinator =>
            {
                var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
                { AppliedPlacements = [Placement(Policy(Prepared() with { Window = new(request, window.NativeThreadId, original) }))] };
                await store.SaveAsync(state, CancellationToken.None);
                var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
                if (delayedCommit) committer.PauseNext();
                await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
                Task<HostManagerRollbackStateDocument>? preparation = null;
                Task<HostManagerRollbackStateDocument>? completed = null;
                var running = executor.RunAsync(request, prepared =>
                {
                    preparation = InvokeWindowOwner(coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
                    return preparation;
                });
                try
                {
                    if (delayedCommit) await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                    var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.NotNull(preparation);
                    Assert.Same(preparation, executor.PersistenceCompletion);
                    Assert.NotNull(result.Prepared);
                    WriteWindowRun(window, request, result, executor);
                    Assert.True(result.Cleanup.Complete);
                    Assert.Equal(delayedCommit, result.PersistencePending);
                    Assert.Equal(!delayedCommit, result.AuthorizationMayHaveBeenSent);
                    Assert.Equal(delayedCommit ? GpuWindowActionOutcome.NotExecuted : GpuWindowActionOutcome.WindowRestored, result.Outcome);
                    if (delayedCommit)
                    {
                        Assert.False(preparation.IsCompleted);
                        Assert.Empty(window.Requests);
                        Assert.Empty(window.Changes);
                        Assert.Equal(original, await ReadOwnedWindow(window));
                    }
                    else
                    {
                        Assert.Equal(0U, result.Cleanup.ExitCode);
                        Assert.Equal(new[] { (81, 60), (80, 60) }, window.Changes);
                    }
                    completed = InvokeWindowOwner(coordinator, "SaveGpuWindowResultAsync", key,
                        GpuWindowActionRecord.Create(result.Prepared).RecordId, result, CancellationToken.None);
                    if (delayedCommit)
                    {
                        Assert.False(completed.IsCompleted);
                        Assert.False(await SettleWindowCheckpoint(coordinator));
                    }
                    committer.Release();
                    await completed.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(await SettleWindowCheckpoint(coordinator));
                    var fact = Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
                    Assert.True(GpuWindowActionRecord.TryRead(fact, out var decoded));
                    Assert.Equal(result, decoded.Result);
                    Assert.Equal(delayedCommit, GpuWindowActionRecord.BlocksProcess(ReadCanonical(path).AppliedPlacements,
                        "changed-adapter", request.ProcessId, (ulong)request.CreationFileTimeUtc));
                    Assert.Equal(original, await ReadOwnedWindow(window));
                    Assert.Equal(delayedCommit ? 0 : 2, window.Requests.Length);
                    output.WriteLine("nativeWindowOwner=" + JsonSerializer.Serialize(new
                    { path, delayedCommit, samePreparationTask = true, committer.Calls, result, fact, canonicalReadBeforeDispose = true }));
                }
                finally
                {
                    committer.Release();
                    await running.WaitAsync(TimeSpan.FromSeconds(8));
                    if (preparation is not null) await preparation.WaitAsync(TimeSpan.FromSeconds(5));
                    if (completed is not null) await completed.WaitAsync(TimeSpan.FromSeconds(5));
                }
            });
        }
        using (var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
            Assert.Equal(delayedCommit, GpuActionFacts.HasUnsettledActions((await reopened.LoadAsync(CancellationToken.None)).AppliedPlacements));
        ClosePrivateWindow(window, input);
    }
}
