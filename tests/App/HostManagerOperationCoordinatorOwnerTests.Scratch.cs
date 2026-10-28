using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Optimization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task WorkerStopCloseInUseRetainsExactScratchAndRetryClosesIt()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        await using var fixture = await Fixture.CreateInitializedWithScratchAsync(
            length => resource = ledger.CreateScratch(length));
        var workspace = OwnerField<NativeOperationCoordinatorWorkspace>(fixture.Owner, "workspace");
        var store = OwnerField<object>(fixture.Owner, "store");
        var lease = resource!.Acquire();
        try
        {
            var error = await Assert.ThrowsAsync<LedgerException>(
                () => fixture.Owner.StopAsync(CancellationToken.None));
            Assert.Equal(LedgerStatus.InUse, error.Status);
            Assert.True(fixture.Owner.GetHealth().Ready);
            Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.Same(workspace, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(store, OwnerField<object>(fixture.Owner, "store"));
            Assert.Same(workspace, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(store, OwnerField<object>(fixture.Owner, "store"));
            Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.False(OwnerField<bool>(fixture.Owner, "disposed"));
            Assert.Throws<IOException>(() =>
            {
                using var conflict = new FileStream(fixture.EnvelopePath + ".lock",
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally { lease.Dispose(); }
        await fixture.Owner.DisposeAsync();
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "store"));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task CanonicalReplacementFailureRetiresCandidateAndKeepsOldRuntimeAndCurrent()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        await using var fixture = await Fixture.CreateInitializedWithScratchAsync(length =>
        {
            var resource = ledger.CreateScratch(length);
            resources.Add(resource);
            return resource;
        });
        var old = OwnerField<NativeOperationCoordinatorWorkspace>(fixture.Owner, "workspace");
        var published = fixture.Owner.GetPublishedState();
        var desired = NextScratchPlan(fixture);
        File.WriteAllBytes(fixture.EnvelopePath, [0xA5]);
        await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", desired, false, CancellationToken.None);
        Assert.Same(old, OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
        Assert.Equal(2, resources.Count);
        Assert.Throws<ObjectDisposedException>(() => resources[1].Acquire());
        Assert.Equal(resources[0].InLedger ? resources[0].Length : 0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Same(published.Operations, fixture.Owner.GetPublishedState().Operations);
        Assert.Equal(published.ConfigurationGeneration, fixture.Owner.GetPublishedState().ConfigurationGeneration);
        Assert.NotNull(fixture.Owner.LastPlanApplicationError);
        Assert.True(old!.SnapshotLocked().StateRevision > 0);
    }

    [Fact]
    public async Task FiniteCallCloseFailurePreservesBothActiveAndPendingReplacementForDisposeRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        ComputeScratchLease? activeLease = null;
        ComputeScratchLease? candidateLease = null;
        await using var fixture = await Fixture.CreateUnstartedWithScratchAsync(length =>
        {
            var resource = ledger.CreateScratch(length);
            resources.Add(resource);
            if (resources.Count == 2)
            {
                activeLease = resources[0].Acquire();
                candidateLease = resource.Acquire();
            }
            return resource;
        });
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        await using var hold = await AcquireTestWorkspaceAsync(fixture.Owner);
        var active = OwnerField<object>(fixture.Owner, "workspace");
        try
        {
            File.WriteAllBytes(fixture.EnvelopePath, [0xA5]);
            await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", NextScratchPlan(fixture), false, CancellationToken.None);
            var pending = OwnerField<object>(fixture.Owner, "pendingWorkspaceClose");
            Assert.NotNull(pending);
            Assert.NotSame(active, pending);
            var error = await Assert.ThrowsAsync<LedgerException>(() => hold.DisposeAsync().AsTask());
            Assert.Equal(LedgerStatus.InUse, error.Status);
            Assert.Same(active, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(pending, OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
            await Assert.ThrowsAsync<InvalidOperationException>(fixture.SubmitComponentDownloadAsync);
            Assert.Equal(2, resources.Count);
        }
        finally
        {
            candidateLease?.Dispose();
            activeLease?.Dispose();
        }
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(TestBound);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task CandidateCloseInUseKeepsExactPendingReferenceAndBlocksThirdReplacement()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        ComputeScratchLease? held = null;
        await using var fixture = await Fixture.CreateInitializedWithScratchAsync(length =>
        {
            var resource = ledger.CreateScratch(length);
            resources.Add(resource);
            if (resources.Count == 2) held = resource.Acquire();
            return resource;
        });
        try
        {
            var old = OwnerField<object>(fixture.Owner, "workspace");
            File.WriteAllBytes(fixture.EnvelopePath, [0xA5]); // Fail after candidate construction, even when its region is compact.
            await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", NextScratchPlan(fixture), false, CancellationToken.None);
            var pending = OwnerField<NativeOperationCoordinatorWorkspace>(fixture.Owner, "pendingWorkspaceClose");
            Assert.NotNull(pending);
            Assert.Same(old, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Equal(2, resources.Count);
            Assert.Throws<ObjectDisposedException>(() => pending!.SnapshotLocked());
            await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", NextScratchPlan(fixture), false, CancellationToken.None);
            Assert.Equal(2, resources.Count);
            Assert.Same(pending, OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
            Assert.Same(old, OwnerField<object>(fixture.Owner, "workspace"));
            held!.Dispose();
            held = null;
            await fixture.Owner.StopAsync(CancellationToken.None);
            Assert.Same(pending, OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
            await fixture.Owner.DisposeAsync();
            Assert.Null(OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCanonicalAndCurrentRemainNewWhenOldCloseIsInUse(bool recreate)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        ComputeScratchLease? held = null;
        await using var fixture = await Fixture.CreateInitializedWithScratchAsync(length =>
        {
            var resource = ledger.CreateScratch(length);
            resources.Add(resource);
            // Replacement construction follows the old workspace's completed export.
            if (resources.Count == 2) held = resources[0].Acquire();
            return resource;
        });
        var old = OwnerField<object>(fixture.Owner, "workspace");
        try
        {
            var desired = NextScratchPlan(fixture, recreate);
            var previousDeployment = fixture.Deployment.Snapshot.OperationCoordinator;
            await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", desired, false, CancellationToken.None);
            Assert.Equal(2, resources.Count);
            Assert.NotNull(held);
            var deployed = fixture.Deployment.Snapshot.OperationCoordinator;
            Assert.Null(deployed.ActiveAttempt);
            Assert.True(deployed.LastFailure is null, fixture.Owner.LastPlanApplicationError);
            Assert.Equal(previousDeployment.StaleCompletionCount, deployed.StaleCompletionCount);
            Assert.True(deployed.LastSettledAttemptId > previousDeployment.LastSettledAttemptId);
            Assert.Equal(deployed.DesiredPublicationSequence, deployed.AppliedPublicationSequence);
            Assert.Equal(deployed.DesiredBuildSha256, deployed.AppliedBuildSha256);
            Assert.Equal(deployed.DesiredRecreateSha256, deployed.AppliedRecreateSha256);
            Assert.Equal(deployed.DesiredHotPublishSha256, deployed.AppliedHotPublishSha256);
            Assert.NotSame(old, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(old, OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
            Assert.Equal(desired.OperationCoordinator.HotPublish.ConfigurationGeneration,
                fixture.Owner.GetPublishedState().ConfigurationGeneration);
            var envelope = HostManagerOperationCanonicalEnvelopeCodec.Decode(
                File.ReadAllBytes(fixture.EnvelopePath), desired.OperationCoordinator.Recreate.Capacity,
                desired.OperationCoordinator.HotPublish.ConfigurationGeneration);
            Assert.Equal(desired.OperationCoordinator.HotPublish.ConfigurationGeneration,
                envelope.ConfigurationGeneration);
            Assert.True(fixture.Owner.GetHealth().PersistenceFaulted);
            Assert.NotNull(fixture.Owner.LastPlanApplicationError);
        }
        finally { held?.Dispose(); }
        await fixture.Owner.StopAsync(CancellationToken.None);
        Assert.Same(old, OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        await fixture.Owner.DisposeAsync();
        Assert.Null(OwnerField<object>(fixture.Owner, "pendingWorkspaceClose"));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task InitialPreparationSettlesWithoutAllocatingNativeScratch()
    {
        var allocations = 0;
        await using var fixture = await Fixture.CreateUnstartedWithScratchAsync(length =>
        {
            allocations++;
            return OperationScratchFixture.Create(length);
        });
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, allocations);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.NotNull(OwnerField<object>(fixture.Owner, "store"));
        var settled = fixture.Deployment.Snapshot.OperationCoordinator;
        Assert.Null(settled.ActiveAttempt);
        Assert.Null(settled.LastFailure);
        Assert.Equal(settled.DesiredPublicationSequence, settled.AppliedPublicationSequence);
    }

    [Fact]
    public async Task PublicConstructorDiDrainsOperationBeforeExistingSelfManagerClosesLedger()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var writer = new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(
            startedAtOverride: _ => null);
        var captures = 0;
        var services = new ServiceCollection();
        // Deliberately register Owner before its dependencies. Actual public
        // constructor resolution, not registration order, enforces the lifetime.
        services.AddSingleton<HostManagerOperationCoordinatorOwner>();
        services.AddSingleton(fixture.Provider);
        services.AddSingleton(OwnerField<HostManagerOperationCoordinatorRuntime>(fixture.Owner, "deployment")!);
        services.AddSingleton(OwnerField<HostManagerOperationEffectRouter>(fixture.Owner, "effectRouter")!);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Path.Combine(fixture.Root, "Resource Manager-APP")));
        services.AddSingleton<ILogger<HostManagerOperationCoordinatorOwner>>(NullLogger<HostManagerOperationCoordinatorOwner>.Instance);
        services.AddSingleton<ResourceManagerSelfLocalResourceManager>(provider => new(
            writer, provider.GetRequiredService<IDiskUsageTreeStore>(),
            provider.GetRequiredService<ResourceManagerLedgerOwner>(),
            () => { captures++; return []; }, (_, _) => null));
        services.AddSingleton<IDiskUsageTreeStore>(provider =>
            new DiskUsageTreeStore(provider.GetRequiredService<ResourceManagerLedgerOwner>()));
        services.AddSingleton<ResourceManagerLedgerOwner>();
        await using var provider = services.BuildServiceProvider();
        var owner = provider.GetRequiredService<HostManagerOperationCoordinatorOwner>();
        var self = provider.GetRequiredService<ResourceManagerSelfLocalResourceManager>();
        var ledger = provider.GetRequiredService<ResourceManagerLedgerOwner>();
        Assert.Same(self, OwnerField<Func<ulong, IComputeScratch>>(owner, "createScratch")!.Target);
        var entered = NewSignal();
        var stopping = NewSignal();
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "recorded");
        };
        await owner.StartAsync(CancellationToken.None);
        try
        {
            _ = await owner.SubmitAsync(HostManagerOperationRequestCodec.ComponentDownload(
                "fixture-component", new ResourceManager.App.Domain.Components.ComponentActionRequest(true)), CancellationToken.None);
            await entered.Task.WaitAsync(TestBound);
            var disposing = provider.DisposeAsync().AsTask();
            await stopping.Task.WaitAsync(TestBound);
            Assert.False(disposing.IsCompleted);
            Assert.NotNull(OwnerField<object>(owner, "workspace"));
            Assert.Contains(ledger.ReadWorkspaceCapacities(), row => row.WorkArea == "operation-output");
            using (var stillOpen = self.CreateScratch(128)) { }
            release.TrySetResult();
            await disposing.WaitAsync(TestBound);
            Assert.True(OwnerField<bool>(owner, "disposed"));
            Assert.Throws<ObjectDisposedException>(() => ledger.ReadTable());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => self.TickAsync());
            Assert.Equal(0, captures);
            Assert.Empty(writer.Requests);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task EffectsOnlyPublicAppGraphKeepsOneSelfFactoryWithoutOptimizationRuntime()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var capabilities = new StartupCapabilitySet("recorded-effects-only", StartupCapability.RuntimeEffectOwners);
        var writer = new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(startedAtOverride: _ => null);
        using var host = Host.CreateDefaultBuilder()
            .UseContentRoot(Path.Combine(fixture.Root, "Resource Manager-APP"))
            .ConfigureServices(services =>
            {
                services.AddResourceManagerApp(["--no-native-ui"], capabilities);
                Assert.Single(services.Where(row => row.ServiceType == typeof(ResourceManagerSelfLocalResourceManager)));
                Assert.Single(services.Where(row => row.ServiceType == typeof(IProcessResourcePolicyWriter)));
                services.RemoveAll<IProcessResourcePolicyWriter>();
                services.AddSingleton<IProcessResourcePolicyWriter>(writer);
                // Keep the actual app registrations and constructor resolution,
                // substitute only existing external effect/runtime source seams.
                services.RemoveAll<HostManagerOperationEffectRouter>();
                services.AddSingleton(OwnerField<HostManagerOperationEffectRouter>(fixture.Owner, "effectRouter")!);
                services.RemoveAll<RuntimePlanProvider>();
                services.AddSingleton(fixture.Provider);
                services.RemoveAll<HostManagerOperationCoordinatorRuntime>();
                services.AddSingleton(OwnerField<HostManagerOperationCoordinatorRuntime>(fixture.Owner, "deployment")!);
            }).Build();
        var owner = host.Services.GetRequiredService<HostManagerOperationCoordinatorOwner>();
        var self = host.Services.GetRequiredService<ResourceManagerSelfLocalResourceManager>();
        Assert.Same(self, OwnerField<Func<ulong, IComputeScratch>>(owner, "createScratch")!.Target);
        Assert.Null(host.Services.GetService<HostManagerSmartCoordinator>());
        Assert.Same(owner, host.Services.GetRequiredService<ResourceManager.App.Application.Operations.IHostManagerOperationCommandService>());
        Assert.False(self.IsBackgroundWorkerRunning);
        Assert.Equal(0UL, host.Services.GetRequiredService<ResourceManagerLedgerOwner>().ReadTable().RegisteredBytes);
        Assert.Empty(writer.Requests);
    }

    private static CompiledHostManagerPlan NextScratchPlan(Fixture fixture, bool recreate = true)
    {
        var desired = CreateSmallPlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            if (recreate)
            {
                var capacity = root["host_recreate"]!["operation_coordinator"]!["capacity"]!;
                capacity["maximum_operation_count"] = 33;
                capacity["maximum_read_count"] = 33;
                capacity["operation_index_capacity"] = 128;
            }
        });
        if (recreate) Assert.NotEqual(fixture.Plan.OperationCoordinator.Recreate, desired.OperationCoordinator.Recreate);
        else Assert.Equal(fixture.Plan.OperationCoordinator.Recreate, desired.OperationCoordinator.Recreate);
        Assert.NotEqual(fixture.Plan.OperationCoordinator.ConfigurationSha256, desired.OperationCoordinator.ConfigurationSha256);
        var current = fixture.Provider.Current;
        fixture.Provider.Publish(current with
        {
            Version = checked(current.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "operation-scratch-replacement-test",
            HostManager = desired
        });
        Assert.Same(desired, fixture.Provider.Current.HostManager.RequirePublished());
        return desired;
    }

    private sealed partial class Fixture
    {
        internal static Task<Fixture> CreateUnstartedWithScratchAsync(Func<ulong, IComputeScratch> factory)
            => CreateAsync(CreateTemporaryRoot(), CreateSmallPlan(),
                ownsRoot: true, start: false, createScratch: factory);

        internal static async Task<Fixture> CreateInitializedWithScratchAsync(Func<ulong, IComputeScratch> factory)
        {
            var fixture = await CreateUnstartedWithScratchAsync(factory);
            try
            {
                await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync", fixture.Plan, true, CancellationToken.None);
                await HoldWorkspaceForManualWorkersAsync(fixture.Owner);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
    }
}
