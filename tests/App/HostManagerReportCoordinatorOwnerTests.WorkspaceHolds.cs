using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    private readonly ConditionalWeakTable<HostManagerReportCoordinatorOwner, CallHoldingProtectionService> callProtection = new();

    [Fact]
    public async Task ImmediateReportStartStopClosesWorkspaceWithoutAnExplicitEvaluation()
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, workspaceFactory: lifetime.Create);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None).WaitAsync(ScratchTestBound);
        Assert.True(owner.ExecuteTask!.IsCompleted);
        Assert.Null(ReportOwnerField<object>(owner, "runHold"));
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(0, lifetime.Active);
        Assert.Equal(new[] { "open", "close" }, lifetime.Events.Select(item => item.Kind));
    }

    [Fact]
    public async Task StopReleasesRunHoldWhenScheduledExecuteWasCanceledBeforeEntry()
    {
        using var canceledBeforeEntry = new CancellationTokenSource();
        canceledBeforeEntry.Cancel();
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, workspaceFactory: lifetime.Create);
        await using var call = await HoldReportCallAsync(owner);
        var workspace = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;

        // Reconstruct the exact .NET 10 canceled-before-delegate state from the
        // hang dump, without changing the production scheduler or racing it.
        await using var runHold = (IAsyncDisposable)typeof(HostManagerReportCoordinatorOwner)
            .GetMethod("AcquireWorkspaceHold", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [CancellationToken.None])!;
        typeof(HostManagerReportCoordinatorOwner)
            .GetField("runHold", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, runHold);
        typeof(BackgroundService)
            .GetField("_stoppingCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, canceledBeforeEntry);
        typeof(BackgroundService)
            .GetField("_executeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, Task.FromCanceled(canceledBeforeEntry.Token));

        await call.DisposeAsync();
        Assert.True(owner.ExecuteTask!.IsCanceled);
        Assert.Equal(1, lifetime.Active);
        Assert.NotNull(ReportOwnerField<object>(owner, "runHold"));
        await owner.StopAsync(CancellationToken.None).WaitAsync(ScratchTestBound);
        Assert.Null(ReportOwnerField<object>(owner, "runHold"));
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(0, lifetime.Active);
        Assert.Throws<ObjectDisposedException>(() => workspace.Session.Capacity);
        await owner.DisposeAsync().AsTask().WaitAsync(ScratchTestBound);
    }

    [Fact]
    public void NativeWorkspaceCloseTimelineRecordsOnlySuccessfulCloseOnce()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var lifetime = new ReportWorkspaceLifetime();
        using var workspace = lifetime.Create(HostManagerTestPlanFactory.CreatePlan().ReportCoordinator,
            1, 1, ledger.CreateScratch);
        workspace.EnsureScratch(1, 1);
        using (var borrow = workspace.AcquireScratch())
        {
            borrow.Complete();
            Assert.Throws<ResourceManager.Ledger.LedgerException>(workspace.Dispose);
            Assert.Equal(1, lifetime.Active);
            Assert.Equal("open", Assert.Single(lifetime.Events).Kind);
        }
        workspace.Dispose();
        workspace.Dispose();
        Assert.Equal(0, lifetime.Active);
        Assert.Equal(new[] { "open", "close" }, lifetime.Events.Select(item => item.Kind));
        Assert.Throws<ObjectDisposedException>(() => workspace.Session.Capacity);
    }

    [Fact]
    public async Task ConcurrentDuplicateReportStartsRetainOnlyOneRunHold()
    {
        var lifetime = new ReportWorkspaceLifetime();
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, resources, out _, workspaceFactory: lifetime.Create);
        await Task.WhenAll(owner.StartAsync(CancellationToken.None), owner.StartAsync(CancellationToken.None));
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(1, resources.ActiveSubscriptions);
        Assert.Equal(1, lifetime.Active);
        await owner.StopAsync(CancellationToken.None);
        Assert.Equal(0, resources.ActiveSubscriptions);
        Assert.Equal(0, lifetime.Active);
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
    }

    [Fact]
    public async Task IdleReportReadsAndPlanPublicationNeverOpenNativeWorkspace()
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out var provider, workspaceFactory: lifetime.Create);
        await owner.GetReportsAsync(CancellationToken.None);
        await owner.GetReportAsync("absent", CancellationToken.None);
        await owner.RefreshTrustedTargetsAsync(CancellationToken.None);
        owner.GetStatus();
        owner.ReadSoftwareIssueSnapshot();
        provider.Publish(provider.Current with { Version = 2, HostManager = NextReportScratchPlan(1) });
        Assert.Empty(lifetime.Events);
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("trust")]
    [InlineData("dismiss")]
    [InlineData("remove")]
    public async Task StoppedReportMutationClosesNativeWorkspaceBeforeReturning(string operation)
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, workspaceFactory: lifetime.Create);
        var entered = Stopwatch.GetTimestamp();
        switch (operation)
        {
            case "refresh": await owner.RefreshReportsAsync(CancellationToken.None); break;
            case "trust": await owner.TrustReportAsync("absent", CancellationToken.None); break;
            case "dismiss": await owner.DismissReportAsync("absent", CancellationToken.None); break;
            case "remove": await owner.RemoveTrustedTargetAsync("absent", CancellationToken.None); break;
        }
        var returned = Stopwatch.GetTimestamp();
        Assert.NotEmpty(lifetime.Events);
        Assert.Equal(new[] { "open", "close" }, lifetime.Events.Select(item => item.Kind));
        Assert.All(lifetime.Events, item => Assert.InRange(item.Timestamp, entered, returned));
        Assert.Equal(0, lifetime.Active);
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
    }

    [Fact]
    public async Task ReportRunAndOverlappingFiniteCallsKeepOneWorkspaceUntilLastReturn()
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, workspaceFactory: lifetime.Create);
        await owner.StartAsync(CancellationToken.None);
        await owner.RefreshReportsAsync(CancellationToken.None);
        var first = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
        await using var firstCall = await HoldReportCallAsync(owner);
        await using var secondCall = await HoldReportCallAsync(owner);
        await owner.StopAsync(CancellationToken.None);
        Assert.Same(first, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        var opens = lifetime.Events.Count(item => item.Kind == "open");
        await firstCall.DisposeAsync();
        Assert.Same(first, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(opens, lifetime.Events.Count(item => item.Kind == "open"));
        Assert.Equal(1, lifetime.Active);
        await secondCall.DisposeAsync();
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(0, lifetime.Active);
        Assert.Throws<ObjectDisposedException>(() => first.Session.Capacity);
    }

    [Fact]
    public async Task ReportDisposalWaitsForAnAdmittedCallOutsideTheNativeExecutionGate()
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, workspaceFactory: lifetime.Create);
        await using var call = await HoldReportCallAsync(owner);
        var workspace = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
        var closing = owner.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Equal(1, lifetime.Active);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        await call.DisposeAsync();
        await closing.WaitAsync(ScratchTestBound);
        Assert.Equal(0, lifetime.Active);
        Assert.Throws<ObjectDisposedException>(() => workspace.Session.Capacity);
    }

    [Fact]
    public async Task HeldReportPlanRebuildRetiresPreviousWorkspaceAndIdleChangeWaitsForNextCall()
    {
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out var provider, workspaceFactory: lifetime.Create);
        await using (var call = await HoldReportCallAsync(owner))
        {
            var previous = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
            provider.Publish(provider.Current with { Version = 2, HostManager = NextReportScratchPlan(1) });
            Assert.NotSame(previous, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
            Assert.Throws<ObjectDisposedException>(() => previous.Session.Capacity);
            Assert.Equal(1, lifetime.Active);
        }
        var closedCount = lifetime.Events.Count;
        provider.Publish(provider.Current with { Version = 3, HostManager = NextReportScratchPlan(2) });
        Assert.Equal(closedCount, lifetime.Events.Count);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(0, lifetime.Active);
        Assert.Equal(provider.Current.HostManager.ReportCoordinator.ConfigurationSha256,
            ReportOwnerField<CompiledHostManagerReportCoordinatorPlan>(owner, "appliedPlan")!.ConfigurationSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedReportWorkspaceReloadsRealPersistentReportAndTrustWithoutChangingEvidence(bool history)
    {
        // CreateDatabase uses this test instance's isolated temporary content root.
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var resources = new CachedResourceSource();
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(persistence, OperationScratchFixture.Create, resources, out _, workspaceFactory: lifetime.Create);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-2);
        var samples = history ? 5 : 3;
        for (var sample = 0; sample < samples; sample++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(41 + sample, observed.AddSeconds(sample * 10), sample < 3 ? 18 : 5));
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal(0, lifetime.Active);
        }
        var before = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        await owner.TrustReportAsync(before.Id, CancellationToken.None);
        before = (await owner.GetReportAsync(before.Id, CancellationToken.None))!;
        var trust = Assert.Single(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        var rows = await persistence.LoadAsync(CancellationToken.None);
        Assert.Contains(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.Contains(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust);
        // Reopen with the exact same source publication: it must not add another sample.
        await owner.RefreshReportsAsync(CancellationToken.None);
        var after = (await owner.GetReportAsync(before.Id, CancellationToken.None))!;
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Trusted, after.Trusted);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.FirstObservedAt, after.FirstObservedAt);
        Assert.Equal(before.LastObservedAt, after.LastObservedAt);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal(before.Target.SoftwareId, after.Target.SoftwareId);
        Assert.Equal(before.Target.SoftwareName, after.Target.SoftwareName);
        Assert.Equal(trust.Id, Assert.Single(await owner.GetTrustedTargetsAsync(CancellationToken.None)).Id);
        var reloadedRows = await persistence.LoadAsync(CancellationToken.None);
        var reportBefore = Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        var reportAfter = Assert.Single(reloadedRows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.Equal(reportBefore.SampleCount, reportAfter.SampleCount);
        Assert.Equal(reportBefore.ValueSum, reportAfter.ValueSum);
        Assert.Equal(0, lifetime.Active);
        Assert.True(await owner.RemoveTrustedTargetAsync(trust.Id, CancellationToken.None));
        Assert.Empty(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        Assert.Equal(0, lifetime.Active);
    }

    private async Task<HeldReportCall> HoldReportCallAsync(HostManagerReportCoordinatorOwner owner)
    {
        var pause = callProtection.GetValue(owner, _ => throw new InvalidOperationException("Unknown test owner")).PauseNextResponse();
        var call = owner.RefreshReportsAsync(CancellationToken.None);
        try { await pause.Entered.Task.WaitAsync(ScratchTestBound); }
        catch { pause.Release.TrySetResult(); throw; }
        return new HeldReportCall(call, pause);
    }

    private sealed class HeldReportCall(Task call, ProtectionResponsePause pause) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            pause.Release.TrySetResult();
            await call.WaitAsync(ScratchTestBound);
        }
    }

    private sealed class ProtectionResponsePause
    {
        internal int Skip = 1;
        internal int Taken;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CallHoldingProtectionService : EmptyProtectionService
    {
        private readonly AsyncLocal<ProtectionResponsePause?> pending = new();
        internal ProtectionResponsePause PauseNextResponse()
        {
            // Bind to this finite call, so an independently scheduled report run
            // cannot consume the evaluation read or the response pause.
            return pending.Value = new();
        }
        public override async Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(CancellationToken cancellationToken)
        {
            if (pending.Value is { } pause && Interlocked.Decrement(ref pause.Skip) < 0
                && Interlocked.CompareExchange(ref pause.Taken, 1, 0) == 0)
            {
                pause.Entered.TrySetResult();
                await pause.Release.Task.WaitAsync(cancellationToken);
            }
            return [];
        }
    }

    private sealed class ReportWorkspaceLifetime
    {
        internal List<(long Timestamp, string Kind)> Events { get; } = [];
        internal NativeReportCoordinatorWorkspace? LastCreated { get; private set; }
        internal int Active;
        internal NativeReportCoordinatorWorkspace Create(CompiledHostManagerReportCoordinatorPlan plan,
            ulong instanceLow, ulong instanceHigh, Func<ulong, IComputeScratch> createScratch)
        {
            var workspace = new NativeReportCoordinatorWorkspace(plan, instanceLow, instanceHigh, createScratch,
                onClosed: () =>
                {
                    Active--;
                    Events.Add((Stopwatch.GetTimestamp(), "close"));
                });
            Events.Add((Stopwatch.GetTimestamp(), "open"));
            Active++;
            LastCreated = workspace;
            return workspace;
        }
    }
}
