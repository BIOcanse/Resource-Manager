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
    [Fact]
    public async Task OpenGlPreparationOriginalOwnerKeepsFailureFactAndPrimaryErrorWhenDisposalFails()
    {
        var root = NewRoot("opengl-preparation-cleanup-failure");
        var path = Path.Combine(root, "recovery.json");
        var committer = new RejectOpenGlPhaseCommitter("never");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false,
            gpuCallbackRuntime: new WindowsGpuCallbackPreparationRuntime(Path.Combine(root, "absent-worker.exe")));
        using var current = Process.GetCurrentProcess();
        var target = new GpuPlacementProcessInstance(current.Id, checked((ulong)current.StartTime.ToFileTimeUtc()),
            current.ProcessName, Environment.ProcessPath!);
        var record = GpuShimPolicyRecord.Create("gl-cleanup", null, D3d11ProxyShimRuntime.CreateExactPolicyValue(123));
        record = record with { Metadata = new Dictionary<string, string>(record.Metadata!)
        {
            ["processId"] = target.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["processStartKey"] = target.ProcessStartKey.ToString(CultureInfo.InvariantCulture)
        } };
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with { AppliedPlacements = [Placement(record)] };
        await store.SaveAsync(state, default);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        WindowsOpenGlCallbackPreparation? execution = null;
        object? process = null;
        FieldInfo? completionField = null;
        object? originalCompletion = null;
        var pendingCleanup = new TaskCompletionSource<ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution.GpuWindowActionProcessCleanup>();
        committer.BeforeCompletedCommit = () =>
        {
            execution = (WindowsOpenGlCallbackPreparation)typeof(HostManagerSmartCoordinator).GetField("gpuCallbackPreparation", fields)!.GetValue(fixture.Coordinator)!;
            process = typeof(WindowsOpenGlCallbackPreparation).GetField("process", fields)!.GetValue(execution)!;
            completionField = process.GetType().GetField("completion", fields)!;
            originalCompletion = completionField.GetValue(process);
            // Existing process state triggers its real "cleanup still owns handles" failure.
            completionField.SetValue(process, pendingCleanup.Task);
        };
        try
        {
            var running = (Task<(HostManagerRollbackStateDocument State, PreparedOpenGlCallbacks? Source)>)typeof(HostManagerSmartCoordinator)
                .GetMethod("ExecuteOwnedOpenGlPreparationAsync", fields)!.Invoke(fixture.Coordinator,
                    [state, HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]), target, 123UL,
                        new CompiledGpuWindowExecutionLimits(1, 1000, 80, 4096, 590312),
                        checked((ulong)Environment.TickCount64 + 5000), CancellationToken.None])!;
            var failure = await Assert.ThrowsAsync<AggregateException>(() => running.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Contains("OpenGL preparation failed", failure.InnerExceptions[0].Message);
            Assert.Contains("Cleanup completion still owns", failure.InnerExceptions[1].Message);
            var release = (Task)typeof(HostManagerSmartCoordinator).GetField("gpuCallbackRelease", fields)!.GetValue(fixture.Coordinator)!;
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => release));
            using var fact = JsonDocument.Parse(ReadCanonical(path).AppliedPlacements[0].Records[0].Metadata!["openGlPreparation"]);
            Assert.Equal("completed", fact.RootElement.GetProperty("Phase").GetString());
            Assert.NotEmpty(fact.RootElement.GetProperty("ExecutionError").GetString()!);
        }
        finally
        {
            committer.BeforeCompletedCommit = null;
            if (process is not null) completionField!.SetValue(process, originalCompletion);
            if (execution is not null) await execution.DisposeAsync();
            typeof(HostManagerSmartCoordinator).GetField("gpuCallbackRelease", fields)!.SetValue(fixture.Coordinator, Task.CompletedTask);
            _ = await SettleWindowCheckpoint(fixture.Coordinator);
            _ = await SettleWindowCheckpoint(fixture.Coordinator);
        }
    }

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
        Task<HostManagerRollbackStateDocument>? observation = null;
        try
        {
            if (delayedSave)
            {
                await committer.Entered.WaitAsync(TimeSpan.FromSeconds(4));
                stop.Cancel();
                Assert.Null((await running.WaitAsync(TimeSpan.FromSeconds(2))).Source);
                Assert.NotNull(typeof(HostManagerSmartCoordinator).GetField("gpuCallbackPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Coordinator));
                var beforeObservation = File.ReadAllBytes(path);
                observation = fixture.Coordinator.GetStateAsync(default);
                Assert.False(observation.IsCompleted);
                Assert.Equal(beforeObservation, File.ReadAllBytes(path));
            }
        }
        finally { committer.Release(); }
        var completed = await running.WaitAsync(TimeSpan.FromSeconds(8));
        if (observation is not null)
        {
            var observed = await observation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(record.RecordId, Assert.Single(
                Assert.Single(observed.AppliedPlacements).Records).RecordId);
        }
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
        var persistedAfterCompletion = File.ReadAllBytes(path);
        _ = await fixture.Coordinator.GetStateAsync(default);
        _ = await fixture.Coordinator.GetStatusAsync(default);
        Assert.Equal(persistedAfterCompletion, File.ReadAllBytes(path));
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
        internal Action? BeforeCompletedCommit;
        public void Commit(string temporaryPath, string canonicalPath, ReadOnlySpan<byte> expectedImage, bool replaceExisting)
        {
            var state = HostManagerRollbackStateEnvelopeCodec.Decode(expectedImage).State;
            foreach (var placement in state.AppliedPlacements)
            foreach (var record in placement.Records)
            {
                if (record.Metadata?.GetValueOrDefault("openGlPreparation") is not { } text) continue;
                using var fact = JsonDocument.Parse(text);
                if (fact.RootElement.GetProperty("Phase").GetString() == "completed") BeforeCompletedCommit?.Invoke();
                if (fact.RootElement.GetProperty("Phase").GetString() == phase)
                    throw new HostManagerRollbackStateCommitException(HostManagerRollbackStateCommitOutcome.NotCommitted,
                        new IOException("Explicit OpenGL preparation save failure fixture."));
            }
            WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(temporaryPath, canonicalPath, expectedImage, replaceExisting);
        }
    }
}
