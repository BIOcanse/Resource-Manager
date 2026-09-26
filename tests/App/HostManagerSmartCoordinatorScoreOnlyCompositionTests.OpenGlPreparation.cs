using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenGlPreparationOriginalOwnerRetainsMissingWorkerOrDelayedSaveWithoutLateLaunch(bool delayedSave)
    {
        var root = NewRoot("opengl-preparation-owner");
        var path = Path.Combine(root, "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false,
            gpuCallbackRuntime: new WindowsGpuCallbackPreparationRuntime(Path.Combine(root, "absent-worker.exe")));
        using var current = Process.GetCurrentProcess();
        var target = new GpuPlacementProcessInstance(current.Id, checked((ulong)current.StartTime.ToUniversalTime().ToFileTimeUtc()),
            current.ProcessName, Environment.ProcessPath!);
        var record = GpuShimPolicyRecord.Create("opengl-owner", null, D3d11ProxyShimRuntime.CreateExactPolicyValue(123));
        record = record with { Metadata = new Dictionary<string, string>(record.Metadata!)
        {
            ["processId"] = target.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["processStartKey"] = target.ProcessStartKey.ToString(CultureInfo.InvariantCulture)
        } };
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with { AppliedPlacements = [Placement(record)] };
        await store.SaveAsync(state, default);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        using var stop = new CancellationTokenSource();
        if (delayedSave) committer.PauseNext();
        var running = (Task<(HostManagerRollbackStateDocument State, PreparedOpenGlCallbacks? Source)>)typeof(HostManagerSmartCoordinator)
            .GetMethod("ExecuteOwnedOpenGlPreparationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [state, key, target, 123UL, new CompiledGpuWindowExecutionLimits(1, 1000, 80, 4096, 590312),
                checked((ulong)Environment.TickCount64 + 5000), stop.Token])!;
        try
        {
            if (delayedSave)
            {
                await committer.Entered.WaitAsync(TimeSpan.FromSeconds(4));
                stop.Cancel();
                Assert.Null((await running.WaitAsync(TimeSpan.FromSeconds(2))).Source);
                Assert.NotNull(typeof(HostManagerSmartCoordinator).GetField("gpuCallbackPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Coordinator));
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.GetStateAsync(default));
            }
        }
        finally { committer.Release(); }
        var completed = await running.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Null(completed.Source);
        if (!delayedSave) Assert.Contains("openGlPreparation", completed.State.AppliedPlacements[0].Records[0].Metadata!.Keys);
        await (Task)typeof(HostManagerSmartCoordinator).GetMethod("DrainGpuActionCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, null)!;
        await (Task)typeof(HostManagerSmartCoordinator).GetMethod("DrainGpuCallbackPreparationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, null)!;
        var retained = Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records);
        Assert.Equal(record.RecordId, retained.RecordId);
        Assert.True(GpuShimPolicyRecord.TryRead(retained, out var policy));
        Assert.Null(policy.PreviousValue);
        Assert.Equal(D3d11ProxyShimRuntime.CreateExactPolicyValue(123), policy.AppliedValue);
        using var json = JsonDocument.Parse(retained.Metadata!["openGlPreparation"]);
        var fact = json.RootElement;
        Assert.Equal(123UL, fact.GetProperty("CallbackAdapter").GetUInt64());
        Assert.Equal(delayedSave ? "not-executed" : "completed", fact.GetProperty("Phase").GetString());
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("Worker").ValueKind);
        if (delayedSave) Assert.Equal(JsonValueKind.Null, fact.GetProperty("Cleanup").ValueKind);
        else
        {
            Assert.NotEmpty(fact.GetProperty("ExecutionError").GetString()!);
            Assert.False(fact.GetProperty("Cleanup").GetProperty("ProcessStarted").GetBoolean());
            Assert.Equal(0U, fact.GetProperty("Cleanup").GetProperty("ActiveProcessCount").GetUInt32());
        }
        Assert.Null(typeof(HostManagerSmartCoordinator).GetField("gpuCallbackPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Coordinator));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("completed")]
    public async Task OpenGlPreparationOriginalOwnerSeparatesSaveFailureFromConfirmedNativeRelease(string failPhase)
    {
        var root = NewRoot("opengl-preparation-save-failure");
        var path = Path.Combine(root, "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            new RejectOpenGlPhaseCommitter(failPhase), WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false,
            gpuCallbackRuntime: new WindowsGpuCallbackPreparationRuntime(Path.Combine(root, "absent-worker.exe")));
        using var current = Process.GetCurrentProcess();
        var target = new GpuPlacementProcessInstance(current.Id, checked((ulong)current.StartTime.ToFileTimeUtc()),
            current.ProcessName, Environment.ProcessPath!);
        var original = GpuShimPolicyRecord.Create("gl-failed-save", null, D3d11ProxyShimRuntime.CreateExactPolicyValue(123));
        original = original with { Metadata = new Dictionary<string, string>(original.Metadata!)
        {
            ["processId"] = target.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["processStartKey"] = target.ProcessStartKey.ToString(CultureInfo.InvariantCulture)
        } };
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with { AppliedPlacements = [Placement(original)] };
        await store.SaveAsync(state, default);
        var running = (Task<(HostManagerRollbackStateDocument State, PreparedOpenGlCallbacks? Source)>)typeof(HostManagerSmartCoordinator)
            .GetMethod("ExecuteOwnedOpenGlPreparationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [state, HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]), target, 123UL,
                new CompiledGpuWindowExecutionLimits(1, 1000, 80, 4096, 590312), checked((ulong)Environment.TickCount64 + 5000), CancellationToken.None])!;
        await Assert.ThrowsAnyAsync<IOException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True((bool)typeof(HostManagerSmartCoordinator).GetMethod("TrySettleGpuCallbackPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, null)!);
        Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
        Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
        var retained = ReadCanonical(path).AppliedPlacements[0].Records[0];
        Assert.Equal(original.RecordId, retained.RecordId);
        if (failPhase == "pending") Assert.DoesNotContain("openGlPreparation", retained.Metadata!.Keys);
        else
        {
            using var fact = JsonDocument.Parse(retained.Metadata!["openGlPreparation"]);
            Assert.Equal("pending", fact.RootElement.GetProperty("Phase").GetString());
        }
    }

    private sealed class RejectOpenGlPhaseCommitter(string phase) : IHostManagerRollbackStateFileCommitter
    {
        public void Commit(string temporaryPath, string canonicalPath, ReadOnlySpan<byte> expectedImage, bool replaceExisting)
        {
            var state = HostManagerRollbackStateEnvelopeCodec.Decode(expectedImage).State;
            foreach (var placement in state.AppliedPlacements)
            foreach (var record in placement.Records)
            {
                if (record.Metadata?.GetValueOrDefault("openGlPreparation") is not { } text) continue;
                using var fact = JsonDocument.Parse(text);
                if (fact.RootElement.GetProperty("Phase").GetString() == phase)
                    throw new HostManagerRollbackStateCommitException(HostManagerRollbackStateCommitOutcome.NotCommitted,
                        new IOException("Explicit OpenGL preparation save failure fixture."));
            }
            WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(temporaryPath, canonicalPath, expectedImage, replaceExisting);
        }
    }
}
