using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests
{
    [WindowExecutorTheory]
    [InlineData("success")]
    [InlineData("prepare-failure")]
    [InlineData("prepare-ambiguous")]
    [InlineData("result-failure")]
    public async Task WindowLedgerActualHelperUsesTheRealCanonicalPreparationBeforePermission(string scenario)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        var original = await ReadOwnedWindow(window);
        var path = Path.Combine(NewRoot("native-" + scenario), "recovery.json");
        var committer = new ControlledCommitter();
        var preparedRecordSaved = false;
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
                   committer, WindowsHostManagerDurableRootManifestCommitter.Instance))
        {
            var state = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
            await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
            HostManagerAppliedRecord? pending = null;
            var result = await executor.RunAsync(request, async prepared =>
            {
                pending = GpuWindowActionRecord.Create(prepared);
                Assert.Equal(original, prepared.Window.Before);
                if (scenario.StartsWith("prepare-", StringComparison.Ordinal))
                    committer.FailNext = scenario == "prepare-ambiguous"
                        ? HostManagerRollbackStateCommitOutcome.CommitAmbiguous : HostManagerRollbackStateCommitOutcome.NotCommitted;
                var candidate = state with { AppliedPlacements = [Placement(pending)] };
                await store.SaveAsync(candidate, CancellationToken.None);
                var disk = Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records);
                Assert.Equal(pending.RecordId, disk.RecordId);
                Assert.True(GpuWindowActionRecord.TryRead(disk, out var saved));
                Assert.Equal(prepared, saved.Prepared);
                Assert.Null(saved.Result);
                Assert.Equal(original, await ReadOwnedWindow(window));
                Assert.Empty(window.Requests);
                state = candidate;
                preparedRecordSaved = true;
            });
            WriteWindowRun(window, request, result, executor);
            Assert.NotNull(pending);
            if (scenario.StartsWith("prepare-", StringComparison.Ordinal))
            {
                Assert.False(preparedRecordSaved);
                Assert.False(result.AuthorizationMayHaveBeenSent);
                Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
                Assert.Empty(window.Changes);
                Assert.Empty(window.Requests);
                Assert.Equal(scenario == "prepare-ambiguous", GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
            }
            else
            {
                Assert.True(preparedRecordSaved);
                Assert.True(result.AuthorizationMayHaveBeenSent);
                Assert.Equal(GpuWindowActionOutcome.WindowRestored, result.Outcome);
                Assert.Equal(0U, result.Cleanup.ExitCode);
                Assert.False(result.Cleanup.TerminationRequested);
                Assert.Equal(new[] { (81, 60), (80, 60) }, window.Changes);
                var final = GpuWindowActionRecord.Complete(pending, result);
                var candidate = state with { AppliedPlacements = [Placement(final)] };
                if (scenario == "result-failure")
                {
                    committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
                    await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => store.SaveAsync(candidate, CancellationToken.None));
                    Assert.True(GpuWindowActionRecord.TryRead(Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records), out var unresolved));
                    Assert.Null(unresolved.Result);
                }
                else
                {
                    await store.SaveAsync(candidate, CancellationToken.None);
                    Assert.True(GpuWindowActionRecord.TryRead(Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records), out var saved));
                    Assert.Equal(result, saved.Result);
                }
            }
            Assert.Equal(original, await ReadOwnedWindow(window));
            output.WriteLine("nativeWindowLedger=" + JsonSerializer.Serialize(new { path, scenario, preparedRecordSaved, canonical = ReadCanonical(path), canonicalReadBeforeDispose = true }));
        }
        using (var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
        {
            var expectedBlock = scenario is "result-failure" or "prepare-ambiguous";
            Assert.Equal(expectedBlock, GpuWindowActionRecord.BlocksProcess(
                (await reopened.LoadAsync(CancellationToken.None)).AppliedPlacements, "changed-placement",
                request.ProcessId, (ulong)request.CreationFileTimeUtc));
        }
        ClosePrivateWindow(window, input);
    }
}
