using System.Diagnostics;
using System.Reflection;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task PreparationAndIdlePlanPublicationNeverOpenWorkspace()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        Assert.Empty(recording.Workspaces);
        var desired = NextScratchPlan(fixture);
        await fixture.WaitForConfigurationGenerationAsync(desired.OperationCoordinator.HotPublish.ConfigurationGeneration);
        Assert.Empty(recording.Workspaces);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.True(fixture.Owner.GetHealth().Ready);
    }

    [Fact]
    public async Task StoppedSubmitAndCancelEachOpenAndCloseTheirOwnWorkspace()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        var submitEntered = Stopwatch.GetTimestamp();
        var submitted = await fixture.SubmitComponentDownloadAsync();
        var submitReturned = Stopwatch.GetTimestamp();
        var first = Assert.Single(recording.Workspaces);
        Assert.InRange(first.Opened, submitEntered, submitReturned);
        Assert.InRange(first.Closed!.Value, first.Opened, submitReturned);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.True(fixture.Owner.HasPendingOperations);

        var cancelEntered = Stopwatch.GetTimestamp();
        var canceled = await fixture.Owner.CancelAsync(submitted.Id, CancellationToken.None);
        var cancelReturned = Stopwatch.GetTimestamp();
        Assert.NotNull(canceled);
        Assert.True(canceled.CancelRequested);
        Assert.Equal(2, recording.Workspaces.Count);
        var second = recording.Workspaces[1];
        Assert.InRange(second.Opened, cancelEntered, cancelReturned);
        Assert.InRange(second.Closed!.Value, second.Opened, cancelReturned);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
    }

    [Fact]
    public async Task OverlappingStoppedCommandsShareWorkspaceUntilBothReturn()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        var gate = OwnerGate(fixture.Owner);
        await gate.WaitAsync();
        Task<HostManagerOperationSnapshot> first;
        Task<HostManagerOperationSnapshot> second;
        try
        {
            // Both acquisition waits enter before either command can mutate.
            // FIFO semaphore handoff makes each acquisition precede the queued
            // mutation of the first command, so there are two real call holds.
            first = fixture.SubmitComponentDownloadAsync();
            second = fixture.SubmitComponentDownloadAsync();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release(); }
        var results = await Task.WhenAll(first, second).WaitAsync(TestBound);
        Assert.Equal(results[0].Id, results[1].Id);
        var resource = Assert.Single(recording.Workspaces);
        Assert.NotNull(resource.Closed);
        Assert.True(resource.Closed >= resource.Opened);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Single(fixture.Owner.GetRecent());
    }

    [Fact]
    public async Task RunningHoldSpansCommandsAndWorkerDrainThenStopClosesWorkspace()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.StartAsync(CancellationToken.None);
        var original = OwnerField<object>(fixture.Owner, "workspace");
        Assert.Same(original, Assert.Single(recording.Workspaces).Workspace);
        var entered = NewSignal();
        var stopping = NewSignal();
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "drained");
        };
        try
        {
            var submitted = await fixture.SubmitComponentDownloadAsync();
            await entered.Task.WaitAsync(TestBound);
            _ = await fixture.Owner.CancelAsync(submitted.Id, CancellationToken.None);
            Assert.Same(original, OwnerField<object>(fixture.Owner, "workspace"));
            var stopEntered = Stopwatch.GetTimestamp();
            var stop = fixture.Owner.StopAsync(CancellationToken.None);
            await stopping.Task.WaitAsync(TestBound);
            Assert.False(stop.IsCompleted);
            Assert.Contains(recording.Workspaces, resource => resource.Closed is null);
            release.TrySetResult();
            await stop.WaitAsync(TestBound);
            var returned = Stopwatch.GetTimestamp();
            Assert.All(recording.Workspaces, resource => Assert.NotNull(resource.Closed));
            Assert.InRange(recording.Workspaces[^1].Closed!.Value, stopEntered, returned);
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task HeldPlanRebuildsWorkspaceAndKeepsItUntilLastCallHoldReturns()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        await using var hold = await AcquireTestWorkspaceAsync(fixture.Owner);
        var original = OwnerField<object>(fixture.Owner, "workspace");
        var desired = NextScratchPlan(fixture);
        await fixture.WaitForConfigurationGenerationAsync(desired.OperationCoordinator.HotPublish.ConfigurationGeneration);
        Assert.NotSame(original, OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Equal(2, recording.Workspaces.Count);
        Assert.Same(original, recording.Workspaces[0].Workspace);
        Assert.Same(OwnerField<object>(fixture.Owner, "workspace"), recording.Workspaces[1].Workspace);
        Assert.NotNull(recording.Workspaces[0].Closed);
        Assert.Null(recording.Workspaces[1].Closed);
        await hold.DisposeAsync();
        Assert.NotNull(recording.Workspaces[1].Closed);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
    }

    [Fact]
    public async Task DisposeWaitsForAnAdmittedFiniteCallBeforeClosingWorkspaceAndStore()
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        var gate = OwnerGate(fixture.Owner);
        await gate.WaitAsync();
        Task<HostManagerOperationSnapshot> command;
        Task disposal;
        try
        {
            command = fixture.SubmitComponentDownloadAsync();
            disposal = fixture.Owner.DisposeAsync().AsTask();
            Assert.False(command.IsCompleted);
            Assert.False(disposal.IsCompleted);
        }
        finally { gate.Release(); }
        // Disposal can retire admission after the command acquired its hold,
        // before that call can mutate. Its return path must still release it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => command);
        await disposal.WaitAsync(TestBound);
        var resource = Assert.Single(recording.Workspaces);
        Assert.NotNull(resource.Closed);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "store"));
        Assert.True(OwnerField<bool>(fixture.Owner, "disposed"));
    }

    [Fact]
    public async Task WorkspaceFactoryRecordsCloseOnlyAfterNativeCloseRetrySucceeds()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        IComputeScratch? scratch = null;
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(
            recording.Create, length => scratch = ledger.CreateScratch(length));
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        await using var hold = await AcquireTestWorkspaceAsync(fixture.Owner);
        var lifetime = Assert.Single(recording.Workspaces);
        using (var lease = scratch!.Acquire())
        {
            lease.Complete();
            var error = await Assert.ThrowsAsync<ResourceManager.Ledger.LedgerException>(
                () => hold.DisposeAsync().AsTask());
            Assert.Equal(ResourceManager.Ledger.LedgerStatus.InUse, error.Status);
            Assert.Null(lifetime.Closed);
            Assert.Same(lifetime.Workspace, OwnerField<object>(fixture.Owner, "workspace"));
        }
        var retryEntered = Stopwatch.GetTimestamp();
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(TestBound);
        Assert.InRange(lifetime.Closed!.Value, retryEntered, Stopwatch.GetTimestamp());
        await fixture.Owner.DisposeAsync();
        Assert.Equal(1, lifetime.CloseCount);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task LastHoldReopenImportsRealCanonicalStoreAndPreservesTerminalResults()
    {
        var root = CreateTemporaryRoot();
        var plan = CreateSmallPlan();
        try
        {
            string id;
            HostManagerOperationSnapshot terminal;
            await using (var first = await Fixture.CreateAsync(root, plan))
            {
                var originalClock = OwnerField<NativeOperationCoordinatorWorkspace>(first.Owner, "workspace")!.ClockInstanceId;
                id = (await first.SubmitComponentDownloadAsync()).Id;
                terminal = await first.WaitForTerminalAsync(id);
                await first.Owner.StopAsync(CancellationToken.None);
                Assert.Null(OwnerField<object>(first.Owner, "workspace"));
                await first.Owner.StartAsync(CancellationToken.None);
                Assert.Equal(originalClock, OwnerField<NativeOperationCoordinatorWorkspace>(first.Owner, "workspace")!.ClockInstanceId);
                Assert.Equal(terminal, first.Owner.Get(id));
            }
            var durable = LoadEnvelope(root, plan);
            await using var reopened = await Fixture.CreateUnstartedAtAsync(root, plan);
            await reopened.Owner.InitializeAsync(CancellationToken.None);
            Assert.Null(OwnerField<object>(reopened.Owner, "workspace"));
            Assert.Equal(terminal, reopened.Owner.Get(id));
            await reopened.Owner.StartAsync(CancellationToken.None);
            Assert.Equal(terminal, reopened.Owner.Get(id));
            Assert.Equal(durable.SessionInstanceId, reopened.ReadEnvelope().SessionInstanceId);
            Assert.Equal(0, reopened.ComponentExecutor.ExecuteCount);
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Fact]
    public async Task IdleOwnersOfSameCanonicalDirectoryRemainMutuallyExclusiveUntilDispose()
    {
        var root = CreateTemporaryRoot();
        var plan = CreateSmallPlan();
        try
        {
            await using var first = await Fixture.CreateUnstartedAtAsync(root, plan);
            await using var second = await Fixture.CreateUnstartedAtAsync(root, plan);
            await first.Owner.InitializeAsync(CancellationToken.None);
            Assert.Null(OwnerField<object>(first.Owner, "workspace"));
            Assert.Equal(first.EnvelopePath, second.EnvelopePath);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.Owner.InitializeAsync(CancellationToken.None));
            Assert.Contains("already holds the canonical store lease", error.Message);
            Assert.IsType<IOException>(error.InnerException);
            await first.Owner.DisposeAsync();
            await second.Owner.InitializeAsync(CancellationToken.None);
            Assert.True(second.Owner.GetHealth().Ready);
            Assert.Null(OwnerField<object>(second.Owner, "workspace"));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Fact]
    public async Task IdleCapacityShrinkRejectsReopenWithoutQuarantiningOrReplacingValidBank()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        var first = await fixture.SubmitComponentDownloadAsync();
        var second = await fixture.Owner.SubmitAsync(
            HostManagerOperationRequestCodec.ComponentDownload("second-fixture-component", new ComponentActionRequest(true)),
            CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        var bank = File.ReadAllBytes(fixture.EnvelopePath);
        var manifest = File.ReadAllBytes(fixture.EnvelopePath + ".root.manifest");
        var changed = CreateSmallPlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["host_recreate"]!["operation_coordinator"]!["capacity"]!["maximum_operation_count"] = 1;
            root["host_recreate"]!["operation_coordinator"]!["capacity"]!["maximum_domain_count"] = 1;
            root["host_recreate"]!["operation_coordinator"]!["capacity"]!["maximum_action_count"] = 1;
            root["host_recreate"]!["operation_coordinator"]!["capacity"]!["maximum_read_count"] = 1;
            root["hot_publish"]!["operation_coordinator"]!["maximum_recent_terminal_count"] = 1;
            root["hot_publish"]!["operation_coordinator"]!["maximum_start_actions_per_plan"] = 1;
            root["hot_publish"]!["operation_coordinator"]!["maximum_cancel_actions_per_plan"] = 1;
            root["hot_publish"]!["operation_coordinator"]!["maximum_recover_actions_per_plan"] = 1;
        });
        fixture.Provider.Publish(fixture.Provider.Current with
        {
            Version = 2,
            HostManager = changed,
            Reason = "idle-operation-capacity-shrink"
        });
        await fixture.WaitForConfigurationGenerationAsync(changed.OperationCoordinator.HotPublish.ConfigurationGeneration);
        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireTestWorkspaceAsync(fixture.Owner));
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Equal(bank, File.ReadAllBytes(fixture.EnvelopePath));
        Assert.Equal(manifest, File.ReadAllBytes(fixture.EnvelopePath + ".root.manifest"));
        Assert.Empty(OperationBackups(fixture.EnvelopePath));
        Assert.Equal(2, fixture.Owner.GetRecent().Count);
        Assert.NotNull(fixture.Owner.Get(first.Id));
        Assert.NotNull(fixture.Owner.Get(second.Id));
        Assert.False(OwnerField<HostManagerOperationCanonicalStore>(fixture.Owner, "store")!.HasUnresolvedLoad);
        Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
    }

    private static async Task<IAsyncDisposable> AcquireTestWorkspaceAsync(HostManagerOperationCoordinatorOwner owner)
    {
        var value = typeof(HostManagerOperationCoordinatorOwner)
            .GetMethod("AcquireWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [CancellationToken.None])!;
        var task = (Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!;
        await task;
        return (IAsyncDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task HoldWorkspaceForManualWorkersAsync(HostManagerOperationCoordinatorOwner owner)
    {
        var hold = await AcquireTestWorkspaceAsync(owner);
        typeof(HostManagerOperationCoordinatorOwner)
            .GetField("runWorkspaceHold", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, hold);
    }

    private sealed partial class Fixture
    {
        internal static Task<Fixture> CreateUnstartedWithWorkspaceAsync(
            HostManagerOperationCoordinatorOwner.WorkspaceFactory factory,
            Func<ulong, IComputeScratch>? scratch = null)
            => CreateAsync(CreateTemporaryRoot(), CreateSmallPlan(), ownsRoot: true,
                start: false, createScratch: scratch, createWorkspace: factory);
    }

    private sealed class WorkspaceLifetimes
    {
        internal List<WorkspaceLifetime> Workspaces { get; } = [];
        internal NativeOperationCoordinatorWorkspace Create(
            CompiledHostManagerOperationCoordinatorPlan plan,
            NativeOperationHandle128 sessionId,
            NativeOperationHandle128 clockId,
            Func<ulong, IComputeScratch> createScratch)
        {
            var lifetime = new WorkspaceLifetime();
            var workspace = new NativeOperationCoordinatorWorkspace(plan, sessionId, clockId, createScratch,
                () =>
                {
                    lifetime.Closed = Stopwatch.GetTimestamp();
                    lifetime.CloseCount++;
                });
            lifetime.Workspace = workspace;
            lifetime.Opened = Stopwatch.GetTimestamp();
            Workspaces.Add(lifetime);
            return workspace;
        }
    }

    private sealed class WorkspaceLifetime
    {
        internal NativeOperationCoordinatorWorkspace Workspace { get; set; } = null!;
        internal long Opened { get; set; }
        internal long? Closed { get; set; }
        internal int CloseCount { get; set; }
    }
}
