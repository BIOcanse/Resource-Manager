using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    private static readonly TimeSpan ScratchTestBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RealRegionRemainsInUseAcrossPersistenceAndQueuedDirectIsRejectedOnDisposal()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        var lifetime = new ReportWorkspaceLifetime();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _, workspaceFactory: lifetime.Create);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None); // Direct continues through the original owner.
        persistence.BlockNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        var direct = owner.RefreshReportsAsync(CancellationToken.None);
        await persistence.Entered.Task.WaitAsync(ScratchTestBound);
        try
        {
            // Reopening may block during initial settlement, before the candidate
            // is committed to owner.workspace. Inspect the real staged native resource.
            var active = lifetime.LastCreated!;
            Assert.Equal(1, lifetime.Active);
            Assert.True(active.Session.Capacity.ReportOutputCapacity > 0);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(active.Layout.Length >= ledger.NativePageSize ? active.Layout.Length : 0,
                ledger.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, ledger.Tick().Released); // Includes read after await, not only P/Invoke.
            var queued = owner.RefreshReportsAsync(CancellationToken.None);
            var closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.True(active.Session.Capacity.ReportOutputCapacity > 0);
            persistence.Release.TrySetResult();
            var committed = await direct.WaitAsync(ScratchTestBound);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            await closing.WaitAsync(ScratchTestBound);
            Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
            Assert.Equal(0, lifetime.Active);
            Assert.Throws<ObjectDisposedException>(() => active.Session.Capacity);
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            // The direct call's release was the last one: it wrote the deferred
            // counters and republished the same status before closing. The record
            // compares UnavailableRules by reference, so compare that list by content.
            var status = owner.GetStatus();
            Assert.Equal(committed.Status.UnavailableRules, status.UnavailableRules);
            Assert.Equal(committed.Status with { UnavailableRules = Array.Empty<string>() },
                status with { UnavailableRules = Array.Empty<string>() });
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        }
        finally { persistence.Release.TrySetResult(); }
    }

    [Fact]
    public async Task FailedPersistenceReturnsLeaseWithoutChangingCanonicalOrPublishedModelAndCanRecover()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        // The run's first evaluation may be canceled by the immediate stop (it is under load), which would leave the
        // published rule counts unevaluated. Complete one evaluation first, so the failure is compared against a
        // published evaluation, not against the pre-evaluation counts.
        var evaluated = await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal("completed", evaluated.Status.EvaluationState);
        var before = await inner.LoadAsync(CancellationToken.None);
        var status = owner.GetStatus();
        persistence.FailNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        var failureOverview = await owner.GetReportsAsync(CancellationToken.None);
        Assert.Equal("failed", failureOverview.Status.EvaluationState);
        Assert.Equal(status.ActiveReportCount, failureOverview.Status.ActiveReportCount);
        Assert.Equal(status.AvailableRuleCount, failureOverview.Status.AvailableRuleCount);
        var after = await inner.LoadAsync(CancellationToken.None);
        Assert.Equal(before.Select(row => (row.OperationKind, row.IdentityHandle, row.MutationVersion)),
            after.Select(row => (row.OperationKind, row.IdentityHandle, row.MutationVersion)));
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released); // No use leaked by exception.
        var recovered = await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.NotNull(recovered.Status.LastEvaluationAt);
        Assert.Equal("completed", recovered.Status.EvaluationState);
        Assert.Equal(ReportLedgerBytes(owner, ledger), ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task CancellationReturnsUseAndDoesNotPublishTheUncommittedResult()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        var status = owner.GetStatus();
        persistence.BlockNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        using var cancellation = new CancellationTokenSource();
        var direct = owner.RefreshReportsAsync(cancellation.Token);
        await persistence.Entered.Task.WaitAsync(ScratchTestBound);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => direct);
        Assert.Equal(status, owner.GetStatus());
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
        persistence.Release.TrySetResult();
        await owner.RefreshReportsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RealReclaimAndPoisonRefillPreserveNativeReportHistoryAndIndependentPublishedValues()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var sample = 0; sample < 3; sample++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(41 + sample, observedAt.AddSeconds(10 * sample), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var saved = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        var before = Assert.Single(saved.NativeReportById.Values);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
        var workspace = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
        using (var lease = workspace.AcquireScratch())
        {
            using var view = new ComputeScratchView(lease);
            view.Column<byte>(0, checked((int)workspace.Layout.Length)).Fill(0xA5);
            lease.Complete();
        }
        resources.Publish(CreateSoftwareMemorySnapshot(44, observedAt.AddSeconds(30), 18));
        await owner.RefreshReportsAsync(CancellationToken.None);
        var after = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.Equal(before.ReportHandle, Assert.Single(after.NativeReportById.Values).ReportHandle);
        Assert.Equal(before.CurrentValue, Assert.Single(saved.NativeReportById.Values).CurrentValue);
        Assert.Equal(before.SampleCount, Assert.Single(saved.NativeReportById.Values).SampleCount);
        Assert.True(Assert.Single(after.NativeReportById.Values).SampleCount > before.SampleCount);
    }

    [Fact]
    public async Task AcceptedReplacementKeepsAnInUseOldWorkspaceReachableAndRejectsAThirdCandidate()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            ledger.CreateScratch, new CachedResourceSource(), out var provider);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var old = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
        using (var borrow = old.AcquireScratch())
        {
            borrow.Complete();
            var next = NextReportScratchPlan(1);
            provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = next });
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Same(old, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "pendingWorkspaceClose"));
            Assert.NotSame(old, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
            Assert.Equal(next.ReportCoordinator.ConfigurationSha256,
                ReportOwnerField<CompiledHostManagerReportCoordinatorPlan>(owner, "appliedPlan")!.ConfigurationSha256);
            var accepted = owner.GetStatus();
            provider.Publish(CompiledRuntimePlan.Default with { Version = 3, HostManager = NextReportScratchPlan(2) });
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RefreshReportsAsync(CancellationToken.None));
            Assert.Equal("failed", owner.GetStatus().EvaluationState);
            Assert.Equal(accepted.ActiveReportCount, owner.GetStatus().ActiveReportCount);
            Assert.Equal(accepted.ConfiguredRuleCount, owner.GetStatus().ConfiguredRuleCount);
            Assert.Equal(LedgerStatus.InUse,
                (await Assert.ThrowsAsync<LedgerException>(() => heldCall.DisposeAsync().AsTask())).Status);
            Assert.Equal(LedgerStatus.InUse,
                (await Assert.ThrowsAsync<LedgerException>(() => owner.DisposeAsync().AsTask())).Status);
            Assert.Same(old, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "pendingWorkspaceClose"));
        }
        await owner.DisposeAsync();
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "pendingWorkspaceClose"));
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task SynchronousDisposeRejectsAnActiveOwnerWithoutRetiringIt()
    {
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        await using var owner = ScratchOwner(persistence, OperationScratchFixture.Create, new CachedResourceSource(), out _);
        await owner.StartAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Equal(0, ReportOwnerField<int>(owner, "retiring"));
        await owner.RefreshReportsAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        owner.Dispose();
        owner.Dispose();
    }

    [Fact]
    public async Task CancelledStopKeepsInFlightLeaseAndAsyncCloseJoinsBeforeDestroyingNativeState()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        using var paused = new PausedResourceSource(resources);
        paused.PauseNextRead();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, paused, out _);
        await owner.StartAsync(CancellationToken.None);
        await paused.ReadPaused.WaitAsync(ScratchTestBound);
        persistence.BlockNext = true;
        persistence.IgnoreWaitCancellation = true;
        paused.Release();
        await persistence.Entered.Task.WaitAsync(ScratchTestBound);
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await owner.StopAsync(cancelled.Token); // Native BackgroundService returns on caller cancellation.
            Assert.False(owner.ExecuteTask!.IsCompleted);
            Assert.NotNull(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            var closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            persistence.Release.TrySetResult();
            await closing.WaitAsync(ScratchTestBound);
            Assert.True(owner.ExecuteTask.IsCompleted);
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        }
        finally { paused.Release(); persistence.Release.TrySetResult(); }
    }

    [Fact]
    public async Task DisposalJoinsAStartAlreadyPastItsLastCheckBeforeBaseTaskInstallation()
    {
        using var metric = new BlockingReportAcquire();
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out _, metric);
        var startup = Task.Run(() => owner.StartAsync(CancellationToken.None));
        await metric.Entered.Task.WaitAsync(ScratchTestBound);
        try
        {
            var closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            metric.Release.Set();
            await startup.WaitAsync(ScratchTestBound);
            await closing.WaitAsync(ScratchTestBound);
            Assert.True(owner.ExecuteTask!.IsCompleted);
            Assert.True(ReportOwnerField<bool>(owner, "disposed"));
        }
        finally { metric.Release.Set(); }
    }

    [Fact]
    public async Task FailedCandidateDisposesOnlyCandidateAndKeepsThePreviousWorkspaceAndCurrent()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var handles = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        await using var owner = ScratchOwner(persistence, length =>
        {
            var handle = ledger.CreateScratch(length);
            handles.Add(handle);
            return handle;
        }, new CachedResourceSource(), out var provider);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        // Stop can cancel the first hosted cycle mid-persist; settle it so the injected failure reaches the candidate.
        await owner.RefreshReportsAsync(CancellationToken.None);
        var old = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace");
        var oldHandle = Assert.Single(handles.Where(static handle => !handle.Closed));
        var handleCount = handles.Count;
        var status = owner.GetStatus();
        persistence.FailNext = true;
        var publication = provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = NextReportScratchPlan(1) });
        Assert.True(publication.HasDeliveryFailures);
        Assert.Contains(publication.DeliveryFailures, failure => failure.Message == "recorded-persist-failure");
        Assert.Same(old, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal("failed", owner.GetStatus().EvaluationState);
        Assert.Equal(status.ActiveReportCount, owner.GetStatus().ActiveReportCount);
        Assert.Equal(status.ConfiguredRuleCount, owner.GetStatus().ConfiguredRuleCount);
        Assert.Equal(handleCount + 1, handles.Count);
        Assert.False(oldHandle.Closed);
        Assert.True(handles[^1].Closed);
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "pendingWorkspaceClose"));
    }

    [Fact]
    public async Task ProductionRegistrationUsesOneSelfAndAsyncDiClosesReportBeforeTheBorrowedLedger()
    {
        var declarations = new ServiceCollection();
        declarations.AddResourceManagerApp(["--no-native-ui"], new("report-test", StartupCapability.OptimizationRuntime));
        var descriptor = Assert.Single(declarations.Where(row => row.ServiceType == typeof(HostManagerReportCoordinatorOwner)));
        Assert.Single(declarations.Where(row => row.ServiceType == typeof(ResourceManagerSelfLocalResourceManager)));
        var deployment = new HostManagerDeploymentState();
        var planProvider = new RuntimePlanProvider(deployment);
        planProvider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var captures = 0;
        var writer = new RejectingReportPolicyWriter();
        IServiceCollection services = new ServiceCollection();
        services.Add(descriptor); // Execute the actual production factory, not a copied test factory.
        services.AddSingleton(planProvider);
        services.AddSingleton(new HostManagerReportCoordinatorRuntime(deployment));
        services.AddSingleton(new HostManagerRuntimeIdentity());
        services.AddSingleton<INativeReportCoordinatorPersistenceStore>(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        services.AddSingleton<IMetricSnapshotObservationSource>(new CachedMetricSource());
        services.AddSingleton<IResourceBreakdownObservationSource>(new CachedResourceSource());
        services.AddSingleton<ISchedulingProcessFactObservationSource>(new BackgroundProcessSource());
        services.AddSingleton<ResourceManager.App.Application.Software.ISoftwareRegistryView>(new RegisteredSoftware());
        services.AddSingleton<ISystemInterruptSnapshotSource>(new UnavailableInterruptSource());
        services.AddSingleton<IOptimizationProtectionService>(new EmptyProtectionService());
        services.AddSingleton<ILogger<HostManagerReportCoordinatorOwner>>(NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        services.AddSingleton<ResourceManagerSelfLocalResourceManager>(provider => new(writer,
            provider.GetRequiredService<IDiskUsageTreeStore>(), provider.GetRequiredService<ResourceManagerLedgerOwner>(),
            () => { captures++; return []; }, (_, _) => null));
        services.AddSingleton<IDiskUsageTreeStore>(provider => new DiskUsageTreeStore(provider.GetRequiredService<ResourceManagerLedgerOwner>()));
        services.AddSingleton<ResourceManagerLedgerOwner>();
        await using var provider = services.BuildServiceProvider();
        var owner = provider.GetRequiredService<HostManagerReportCoordinatorOwner>();
        var ledger = provider.GetRequiredService<ResourceManagerLedgerOwner>();
        await owner.StartAsync(CancellationToken.None);
        Assert.Equal(ReportLedgerBytes(owner, ledger), ledger.ReadTable().RegisteredBytes);
        await provider.DisposeAsync(); // IAsyncDisposable drains the actual running BackgroundService.
        Assert.True(ReportOwnerField<bool>(owner, "disposed"));
        Assert.Throws<ObjectDisposedException>(() => ledger.ReadTable());
        Assert.Equal(0, captures);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public void ReadOnlyGraphDoesNotGainSelfPolicyWriterOrReportOwner()
    {
        var services = new ServiceCollection();
        services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        Assert.DoesNotContain(services, row => row.ServiceType == typeof(ResourceManagerSelfLocalResourceManager));
        Assert.DoesNotContain(services, row => row.ServiceType == typeof(IProcessResourcePolicyWriter));
        Assert.DoesNotContain(services, row => row.ServiceType == typeof(HostManagerReportCoordinatorOwner));
        Assert.Single(services.Where(row => row.ServiceType == typeof(ResourceManagerLedgerOwner)));
    }

    [Fact]
    public async Task PendingPersistenceReplaysBeforeANewSourceFrameAndKeepsEverySample()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var generation = 41; generation <= 43; generation++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(generation, observed.AddSeconds((generation - 41) * 10), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        var before = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel");
        persistence.FailNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(44, observed.AddSeconds(30), 18));
        await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        var failureModel = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.Equal("failed", failureModel.Status.EvaluationState);
        Assert.Equal(before!.Reports, failureModel.Reports);
        Assert.Equal(before.NativeReportById, failureModel.NativeReportById);
        resources.Publish(CreateSoftwareMemorySnapshot(45, observed.AddSeconds(40), 18));
        await owner.RefreshReportsAsync(CancellationToken.None);
        var after = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.NotSame(before, after);
        Assert.Equal(5UL, Assert.Single(after.NativeReportById.Values).SampleCount);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
    }

    [Fact]
    public async Task TrustAndRemoveEachReplayPendingPersistenceBeforeTheirNativeMutation()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var persistence = new ControlledReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var generation = 41; generation <= 43; generation++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(generation, observed.AddSeconds((generation - 41) * 10), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var original = await owner.GetReportsAsync(CancellationToken.None);
        var report = Assert.Single(original.Reports);
        persistence.FailNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(44, observed.AddSeconds(30), 18));
        await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        var trusted = await owner.TrustReportAsync(report.Id, CancellationToken.None);
        Assert.NotNull(trusted);
        Assert.Equal(trusted.Id, Assert.Single(await owner.GetTrustedTargetsAsync(CancellationToken.None)).Id);
        resources.Publish(CreateSoftwareMemorySnapshot(45, observed.AddSeconds(40), 18));
        await owner.RefreshReportsAsync(CancellationToken.None);
        persistence.FailNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(46, observed.AddSeconds(50), 18));
        await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        Assert.True(await owner.RemoveTrustedTargetAsync(trusted.Id, CancellationToken.None));
        Assert.Empty(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
    }

    [Fact]
    public async Task DesiredReplacementDrainsPendingPersistenceBeforeLoadingItsCanonicalRows()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out var provider);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var generation = 41; generation <= 43; generation++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(generation, observed.AddSeconds((generation - 41) * 10), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        var old = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace");
        persistence.FailNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(44, observed.AddSeconds(30), 18));
        await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        var next = NextReportScratchPlan(1);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = next });
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.NotSame(old, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "pendingWorkspaceClose"));
        Assert.Equal(next.ReportCoordinator.ConfigurationSha256,
            ReportOwnerField<CompiledHostManagerReportCoordinatorPlan>(owner, "appliedPlan")!.ConfigurationSha256);
        var current = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.Equal(4UL, Assert.Single(current.NativeReportById.Values).SampleCount);
        Assert.Contains(await inner.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation && row.SampleCount == 4UL);
    }

    [Fact]
    public async Task CommittedStoreFailureReplaysIdempotentlyWithoutDuplicatingTheNativeSample()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var generation = 41; generation <= 43; generation++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(generation, observed.AddSeconds((generation - 41) * 10), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        var before = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel");
        persistence.FailAfterCommitNext = true;
        resources.Publish(CreateSoftwareMemorySnapshot(44, observed.AddSeconds(30), 18));
        var failure = await Assert.ThrowsAsync<IOException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        Assert.Equal("recorded-after-commit-failure", failure.Message);
        var failureModel = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.Equal("failed", failureModel.Status.EvaluationState);
        Assert.Equal(before!.Reports, failureModel.Reports);
        Assert.Equal(before.NativeReportById, failureModel.NativeReportById);
        var committed = (await inner.LoadAsync(CancellationToken.None))
            .OrderBy(row => row.OperationKind).ThenBy(row => row.IdentityHandle).ToArray();
        // The active report row goes on every plan; its observation's growing
        // counters wait for the counter checkpoint while the call is held.
        Assert.Contains(committed,
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report && row.SampleCount == 4UL);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
        await owner.RefreshReportsAsync(CancellationToken.None);
        var current = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        Assert.NotSame(before, current);
        Assert.Equal(4UL, Assert.Single(current.NativeReportById.Values).SampleCount);
        var recovered = (await inner.LoadAsync(CancellationToken.None))
            .OrderBy(row => row.OperationKind).ThenBy(row => row.IdentityHandle).ToArray();
        Assert.Equal(System.Runtime.InteropServices.MemoryMarshal.AsBytes(committed.AsSpan()).ToArray(),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(recovered.AsSpan()).ToArray());
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
    }

    [Fact]
    public async Task TrustProjectionPersistsDerivedReportBeforePublishingAndRecoversItsFailedBatch()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        await using var heldCall = await HoldReportCallAsync(owner);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var generation = 41; generation <= 43; generation++)
        {
            resources.Publish(CreateSoftwareMemorySnapshot(generation, observed.AddSeconds((generation - 41) * 10), 18));
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var report = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        Assert.Equal(OptimizationReportStates.Active, report.State);
        var before = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
        persistence.FailTrustedReportBatchOnce = true;
        var failure = await Assert.ThrowsAsync<IOException>(() => owner.TrustReportAsync(report.Id, CancellationToken.None));
        Assert.Equal("recorded-derived-report-failure", failure.Message);
        Assert.False(persistence.FailTrustedReportBatchOnce);
        Assert.Same(before, ReportOwnerField<HostManagerReportReadModel>(owner, "readModel"));
        Assert.Equal(OptimizationReportStates.Active, (await owner.GetReportAsync(report.Id, CancellationToken.None))!.State);
        Assert.Empty(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        var failedBatch = persistence.FailedTrustedReportBatch!;
        Assert.NotEmpty(failedBatch);
        Assert.DoesNotContain(failedBatch, row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust);
        var derived = Assert.Single(failedBatch, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.NotEqual(0U, derived.Flags & (uint)NativeReportPersistenceFlags.Trusted);
        var partialRows = await inner.LoadAsync(CancellationToken.None);
        Assert.Single(partialRows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust);
        Assert.Equal(0U, Assert.Single(partialRows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report)
            .Flags & (uint)NativeReportPersistenceFlags.Trusted);
        var workspace = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;
        Assert.NotNull(workspace.PendingPlan);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);

        var recovered = await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Null(workspace.PendingPlan);
        Assert.Equal(OptimizationReportStates.Active, Assert.Single(recovered.Reports).State);
        Assert.True(Assert.Single(recovered.Reports).Trusted);
        Assert.Equal(1, recovered.Status.ActiveReportCount);
        Assert.Empty(owner.ReadSoftwareIssueSnapshot().Signals);
        Assert.True((await owner.GetReportAsync(report.Id, CancellationToken.None))!.Trusted);
        var trust = Assert.Single(recovered.TrustedTargets);
        var stored = Assert.Single(await inner.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.Equal(derived.IdentityHandle, stored.IdentityHandle);
        Assert.Equal(derived.MutationVersion, stored.MutationVersion);
        Assert.NotEqual(0U, stored.Flags & (uint)NativeReportPersistenceFlags.Trusted);

        Assert.True(await owner.RemoveTrustedTargetAsync(trust.Id, CancellationToken.None));
        Assert.Equal(OptimizationReportStates.Active, Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports).State);
        Assert.Equal(OptimizationReportStates.Active, (await owner.GetReportAsync(report.Id, CancellationToken.None))!.State);
        Assert.False((await owner.GetReportAsync(report.Id, CancellationToken.None))!.Trusted);
        Assert.Single(owner.ReadSoftwareIssueSnapshot().Signals);
        Assert.Empty(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        Assert.Equal(0U, Assert.Single(await inner.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report)
            .Flags & (uint)NativeReportPersistenceFlags.Trusted);
        Assert.Null(workspace.PendingPlan);
        Assert.Equal(ReportLedgerBytes(owner, ledger) == 0 ? 0UL : 1UL, ledger.Tick().Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterCommittedTrustAndFailedReportBatchRestoresTrustedRetainedReport(bool history)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        OptimizationReportItem retained;
        NativeReportPersistenceOperation untrustedRow;
        await using (var owner = ScratchOwner(persistence, ledger.CreateScratch, resources, out _))
        {
            var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var sample = 0; sample < (history ? 5 : 3); sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41, observed.AddSeconds(sample * 10), sample < 3 ? 18 : 5));
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            retained = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
            Assert.Equal(history ? OptimizationReportStates.History : OptimizationReportStates.Active, retained.State);
            Assert.False(retained.Trusted);
            persistence.FailTrustedReportBatchOnce = true;

            // Hold the workspace, so the failed derived batch stays pending after the call.
            await using (HoldReportWorkspace(owner))
            {
                var failure = await Assert.ThrowsAsync<IOException>(() => owner.TrustReportAsync(retained.Id, CancellationToken.None));

                Assert.Equal("recorded-derived-report-failure", failure.Message);
                Assert.False(persistence.FailTrustedReportBatchOnce);
                var rows = await inner.LoadAsync(CancellationToken.None);
                Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust);
                untrustedRow = Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
                Assert.Equal(0U, untrustedRow.Flags & (uint)NativeReportPersistenceFlags.Trusted);
                Assert.Equal(history ? 0U : (uint)NativeReportPersistenceFlags.Active,
                    untrustedRow.Flags & (uint)NativeReportPersistenceFlags.Active);
                Assert.False((await owner.GetReportAsync(retained.Id, CancellationToken.None))!.Trusted);
                // The last release writes before closing and would replay the pending
                // derived batch. Fail that write too, so storage keeps only the committed
                // trust and the untrusted report, as after a crash: no same-process recovery.
                persistence.FailNext = true;
            }
            Assert.False(persistence.FailNext);
            Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        }
        Assert.Equal(untrustedRow, Assert.Single(await inner.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report));

        // An unavailable resource source cannot produce evidence or judge the restored lifecycle.
        await using var restarted = ScratchOwner(persistence, ledger.CreateScratch, new CachedResourceSource(), out _);
        var restored = await restarted.RefreshReportsAsync(CancellationToken.None);
        var report = Assert.Single(restored.Reports);

        Assert.Equal(retained.Id, report.Id);
        Assert.Equal(retained.State, report.State);
        Assert.Equal(retained.CreatedAt, report.CreatedAt);
        Assert.Equal(retained.FirstObservedAt, report.FirstObservedAt);
        Assert.Equal(retained.LastObservedAt, report.LastObservedAt);
        Assert.Equal(retained.Evidence, report.Evidence);
        Assert.Equal(retained.Target.SoftwareId, report.Target.SoftwareId);
        Assert.Equal(retained.Target.SoftwareName, report.Target.SoftwareName);
        Assert.True(report.Trusted);
        Assert.True((await restarted.GetReportAsync(retained.Id, CancellationToken.None))!.Trusted);
        Assert.Single(restored.TrustedTargets);
        Assert.Equal(history ? 0 : 1, restored.Status.ActiveReportCount);
        Assert.Empty(restarted.ReadSoftwareIssueSnapshot().Signals);
        var reconciled = Assert.Single(await inner.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.NotEqual(0U, reconciled.Flags & (uint)NativeReportPersistenceFlags.Trusted);
        Assert.Equal(untrustedRow.Flags & (uint)NativeReportPersistenceFlags.Active,
            reconciled.Flags & (uint)NativeReportPersistenceFlags.Active);
        Assert.Equal(untrustedRow.CurrentValue, reconciled.CurrentValue);
        Assert.Equal(untrustedRow.ValueSum, reconciled.ValueSum);
        Assert.Equal(untrustedRow.PeakValue, reconciled.PeakValue);
        Assert.Equal(untrustedRow.SampleCount, reconciled.SampleCount);
    }

    private HostManagerReportCoordinatorOwner ScratchOwner(INativeReportCoordinatorPersistenceStore persistence,
        Func<ulong, IComputeScratch> factory, IResourceBreakdownObservationSource resources, out RuntimePlanProvider provider,
        IMetricSnapshotObservationSource? metrics = null,
        Func<CompiledHostManagerReportCoordinatorPlan, ulong, ulong,
            Func<ulong, IComputeScratch>, NativeReportCoordinatorWorkspace>? workspaceFactory = null)
    {
        var deployment = new HostManagerDeploymentState();
        provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var protection = new CallHoldingProtectionService();
        var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment), new HostManagerRuntimeIdentity(),
            persistence, metrics ?? new CachedMetricSource(), resources, new UnavailableInterruptSource(),
            protection, NullLogger<HostManagerReportCoordinatorOwner>.Instance, factory, new BackgroundProcessSource(), new RegisteredSoftware(), workspaceFactory);
        callProtection.Add(owner, protection);
        return owner;
    }

    private static CompiledHostManagerPlan NextReportScratchPlan(int delta)
        => HostManagerTestPlanFactory.CreatePlan(root =>
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + delta);

    private static ulong ReportLedgerBytes(HostManagerReportCoordinatorOwner owner, ResourceManagerLedgerOwner ledger)
    {
        var length = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")?.Layout.Length ?? 0;
        return length >= ledger.NativePageSize ? length : 0;
    }

    private static T? ReportOwnerField<T>(HostManagerReportCoordinatorOwner owner, string name)
        => (T?)typeof(HostManagerReportCoordinatorOwner).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);

    private sealed class BlockingReportAcquire : IMetricSnapshotObservationSource, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
        {
            Entered.TrySetResult();
            if (!Release.Wait(ScratchTestBound)) throw new TimeoutException("Recorded acquire not released.");
            return new CallbackLease(() => { });
        }
        public ResourceManager.App.Domain.Metrics.HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        public void Dispose() => Release.Dispose();
    }

    private sealed class ControlledReportStore(INativeReportCoordinatorPersistenceStore inner)
        : INativeReportCoordinatorPersistenceStore
    {
        internal bool BlockNext;
        internal bool FailNext;
        internal bool FailAfterCommitNext;
        internal bool FailTrustedReportBatchOnce;
        internal NativeReportPersistenceOperation[]? FailedTrustedReportBatch;
        internal bool IgnoreWaitCancellation;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HostManagerReportTargetDescriptor[]> LoadTargetsAsync(CancellationToken cancellationToken)
            => inner.LoadTargetsAsync(cancellationToken);

        public Task<NativeReportPersistenceOperation[]> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public async Task PersistAsync(ReadOnlyMemory<NativeReportPersistenceOperation> operations, CancellationToken token,
            IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets = null)
        {
            if (FailNext) { FailNext = false; throw new IOException("recorded-persist-failure"); }
            if (FailTrustedReportBatchOnce)
            {
                var batch = operations.ToArray();
                if (!batch.Any(row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust)
                    && batch.Any(row => row.OperationKind == (uint)NativeReportPersistenceKind.Report
                        && (row.Flags & (uint)NativeReportPersistenceFlags.Trusted) != 0))
                {
                    FailTrustedReportBatchOnce = false;
                    FailedTrustedReportBatch = batch;
                    throw new IOException("recorded-derived-report-failure");
                }
            }
            if (BlockNext)
            {
                BlockNext = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(IgnoreWaitCancellation ? CancellationToken.None : token);
            }
            var failAfterCommit = FailAfterCommitNext;
            FailAfterCommitNext = false;
            await inner.PersistAsync(operations, token, targets);
            if (failAfterCommit) throw new IOException("recorded-after-commit-failure");
        }
    }

    // No process lookup or writer is exercised by the report-only composition.
    private sealed class RejectingReportPolicyWriter : IProcessResourcePolicyWriter
    {
        internal int Calls;
        private InvalidOperationException Unexpected()
        {
            Interlocked.Increment(ref Calls);
            return new("Report composition must not query or write an OS process.");
        }
        public ProcessResourcePolicySnapshot? TryReadProcess(int id) => throw Unexpected();
        public DateTimeOffset? TryReadProcessStartedAt(int id) => throw Unexpected();
        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(int id)
            => throw Unexpected();
        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int id, ProcessPlacementReadFields fields) => throw Unexpected();
        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(int id, int thread)
            => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int id, string priority) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int id, long mask) => throw Unexpected();
        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int id) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(int id, IReadOnlyList<uint> sets)
            => throw Unexpected();
        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int id, int thread) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int id, int thread, DateTimeOffset created, IReadOnlyList<uint> sets) => throw Unexpected();
        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int id) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int id, DateTimeOffset started, uint current, uint priority) => throw Unexpected();
        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int id) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(int id, uint control, uint state)
            => throw Unexpected();
        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests) => throw Unexpected();
    }
}
