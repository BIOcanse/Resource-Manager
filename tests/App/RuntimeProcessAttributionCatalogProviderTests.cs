using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using ResourceManager.App.Domain.RuntimeSpecialization;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.ReadWrite.Windows.Processes;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class RuntimeProcessAttributionCatalogProviderTests
{
    private const string UserOne = "S-1-5-21-101-102-103-1001";
    private const string UserTwo = "S-1-5-21-101-102-103-1002";

    [Fact]
    public async Task OwnerCatalogMatchesTheListIdentityClassificationAndScoreOverrideForTwoUsers()
    {
        var registry = new OwnerSoftwareRegistryView();
        using var provider = CreateProvider(registry);
        var tracking = new TrackingCatalogProvider(provider);
        var plan = new CompiledBaseScorePlan(new Dictionary<string, double>
            { ["installed:user-one"] = 83, ["installed:user-two"] = 47 }, new Dictionary<string, double>());
        RuntimeProcessIdentity[] processes = [
            new(51001, null, "app", @"D:\Apps\SharedProduct\app.exe", false, StartKey: 10, UserSid: UserOne),
            new(51002, null, "app", @"D:\Apps\SharedProduct\app.exe", false, StartKey: 20, UserSid: UserTwo),
            new(51003, null, "app", @"D:\Apps\SharedProduct\app.exe", false, StartKey: 30, UserSid: UserOne)];
        var result = await RuntimeProcessAttributionCapture.CaptureAsync(tracking, processes, plan, default);
        Assert.Equal(new[] { UserOne, UserTwo }, tracking.AcquiredScopes);
        Assert.Equal(2, tracking.ReturnedLeases);
        Assert.Equal(2, registry.ReadScopes.Count);
        Assert.Equal(new[] { UserOne, UserTwo }, registry.ReadScopes);
        var one = Assert.Single(await registry.GetSoftwareAsync(UserOne, default));
        var two = Assert.Single(await registry.GetSoftwareAsync(UserTwo, default));
        Assert.Equal(one.Id, result.Batch.SoftwareByProcessId[51001].Id);
        Assert.Equal(one.Kind, result.Batch.SoftwareByProcessId[51003].Kind);
        Assert.Equal(two.Id, result.Batch.SoftwareByProcessId[51002].Id);
        Assert.Equal(two.Kind, result.Batch.SoftwareByProcessId[51002].Kind);
        Assert.Equal(83, plan.ResolveBaseScore(result.Batch.SoftwareByProcessId[51001].Id, one.Kind, null));
        Assert.Equal(47, plan.ResolveBaseScore(result.Batch.SoftwareByProcessId[51002].Id, two.Kind, null));
        Assert.Contains(result.SoftwareBaseScores, score => score.SoftwareId == one.Id && score.BaseScore == 83);
        Assert.Contains(result.SoftwareBaseScores, score => score.SoftwareId == two.Id && score.BaseScore == 47);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("S-1-5-80-1-2-3-4-5")]
    [InlineData(null)]
    public async Task NonUserAndUnreadableOwnersUseOnlyTheMachineView(string? sid)
    {
        var registry = new OwnerSoftwareRegistryView();
        using var provider = CreateProvider(registry);
        using var userCatalog = await provider.AcquireCatalogAsync(UserOne, default);
        registry.ReadScopes.Clear();
        using var output = new StringWriter();
        using var listener = new System.Diagnostics.TextWriterTraceListener(output);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
            var capture = await RuntimeProcessAttributionCapture.CaptureAsync(provider,
                [new(51999, null, "app", @"D:\Apps\SharedProduct\app.exe", false, StartKey: 10, UserSid: sid)], plan, default);
            Assert.Equal("installed:machine", capture.Batch.SoftwareByProcessId[51999].Id);
            Assert.Equal(RuntimeProcessAttributionCapture.MachineScope, Assert.Single(registry.ReadScopes));
            listener.Flush();
            if (sid is null)
            {
                Assert.Contains("owner unavailable for PID 51999, creation 10", output.ToString());
                Assert.Contains("machine-wide", output.ToString());
            }
            else Assert.DoesNotContain("owner unavailable for PID 51999", output.ToString());
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task FailedSecondOwnerAcquisitionReturnsTheFirstOwnersLease()
    {
        using var provider = CreateProvider(new OwnerSoftwareRegistryView());
        var tracking = new TrackingCatalogProvider(provider) { FailScope = UserTwo };
        var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeProcessAttributionCapture.CaptureAsync(tracking,
            [new(54000, null, "app", null, false, StartKey: 10, UserSid: UserOne),
             new(54001, null, "app", null, false, StartKey: 20, UserSid: UserTwo)], plan, default));
        Assert.Equal(new[] { UserOne, UserTwo }, tracking.AcquiredScopes);
        Assert.Equal(1, tracking.ReturnedLeases);
    }

    [Fact]
    public async Task StableProcessInstancesReuseDirectMatchesUntilTheCatalogInputsChange()
    {
        var retainedCatalogs = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        try
        {
            var softwareRegistry = new MutableSoftwareRegistryView(
                [CreateSoftware("software:host", "Host", @"D:\Apps\Host")]);
            var adapterRegistry = new MutableAdapterSoftwareRegistry();
            var controlledRegistry = new MutableControlledSoftwareRegistry();
            var games = new FakeWindowsGameListSource();
            var bundle = new MutableSoftwareIdentityCatalog();
            var packageResolver = new CountingPackageIdentityResolver();
            var (owner, planProvider) = CreateMutableSoftwareIdentityOwner();
            using (owner)
            using (var provider = CreateProvider(softwareRegistry, adapterRegistry, games,
                packageResolver, controlledRegistry, bundle, owner))
            {
                var cache = new DirectAttributionCache();
                var process = new RuntimeProcessIdentity(55001, null, "app", @"D:\Apps\Host\app.exe", false,
                    StartKey: 10, UserSid: UserOne);
                RuntimeProcessIdentity[] processes = [process];
                var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());

                var first = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(1, bundle.Observations);
                var firstAttribution = first.Batch.SoftwareByProcessId[process.ProcessId];
                var unchanged = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(1, bundle.Observations);
                Assert.Equal(firstAttribution, unchanged.Batch.SoftwareByProcessId[process.ProcessId]);
                Assert.Equal(1, packageResolver.Calls);

                var beforeScopeSwitch = bundle.Observations;
                await CaptureCached(provider, [process with { UserSid = UserTwo }], plan, cache, retainedCatalogs);
                Assert.Equal(beforeScopeSwitch + 1, bundle.Observations);
                await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(beforeScopeSwitch + 2, bundle.Observations);

                processes = [process, process with { ProcessId = 55002, StartKey = 20 }];
                var beforeNewInstance = bundle.Observations;
                await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(beforeNewInstance + 1, bundle.Observations);

                async Task AssertCatalogChangeReResolvesEachLiveInstance()
                {
                    var previousGeneration = cache.GenerationFor(new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value));
                    var beforeRefresh = bundle.Observations;
                    var refreshed = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                    Assert.Equal(beforeRefresh + processes.Length, bundle.Observations);
                    Assert.Equal(previousGeneration + 1,
                        cache.GenerationFor(new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value)));
                    var unchangedAfterRefresh = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                    Assert.Equal(beforeRefresh + processes.Length, bundle.Observations);
                    Assert.Equal(refreshed.Batch.SoftwareByProcessId, unchangedAfterRefresh.Batch.SoftwareByProcessId);
                }

                softwareRegistry.SetSoftware(
                    [CreateSoftware("software:host", "Host Updated", @"D:\Apps\Host")]);
                await provider.InvalidateSoftwareSnapshotAsync();
                await AssertCatalogChangeReResolvesEachLiveInstance();

                adapterRegistry.Registrations = [CreateAdapter("adapter:host", "Adapted Host", @"D:\Apps\Host")];
                await AssertCatalogChangeReResolvesEachLiveInstance();

                controlledRegistry.Registrations = [new ControlledSoftwareRegistration(
                    Guid.Parse("10000000-0000-0000-0000-000000000010"), DateTimeOffset.UnixEpoch,
                    "app.exe", ["Host"], [@"D:\Apps\Host"], [], [], "controlled")];
                await AssertCatalogChangeReResolvesEachLiveInstance();

                games.Paths = [@"D:\Apps\Host\app.exe"];
                await provider.InvalidateSoftwareSnapshotAsync();
                await AssertCatalogChangeReResolvesEachLiveInstance();

                PublishSoftwareIdentityPlan(planProvider, 2, root =>
                {
                    root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
                    root["hot_publish"]!["software_identity_catalog"]!["exact_text_score"] = 121;
                });
                await AssertCatalogChangeReResolvesEachLiveInstance();

                PublishSoftwareIdentityPlan(planProvider, 3, root =>
                {
                    root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 2;
                    root["hot_publish"]!["software_identity_catalog"]!["exact_text_score"] = 121;
                    root["hot_publish"]!["software_identity_resolution"]!["source_policies"]![0]!["priority"] = 11;
                });
                await AssertCatalogChangeReResolvesEachLiveInstance();

                bundle.Version = "1.0.1";
                await AssertCatalogChangeReResolvesEachLiveInstance();

                packageResolver.FailProcessId = 55003;
                processes = [.. processes, process with { ProcessId = 55003, StartKey = 30 }];
                var failedBefore = packageResolver.CallsFor(55003);
                var failed = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(failedBefore + 1, packageResolver.CallsFor(55003));
                Assert.Equal("IOException", failed.Batch.Failures[55003]);
                var failedAgain = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(failedBefore + 2, packageResolver.CallsFor(55003));
                Assert.Equal("IOException", failedAgain.Batch.Failures[55003]);

                packageResolver.FailProcessId = null;
                var missingBirth = process with { ProcessId = 55004, StartKey = null };
                processes = [.. processes, missingBirth];
                var missingBirthBefore = packageResolver.CallsFor(55004);
                await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                // Count the missing-birth instance separately from other live input probes.
                Assert.Equal(missingBirthBefore + 1, packageResolver.CallsFor(55004));
                Assert.Equal(failedBefore + 3, packageResolver.CallsFor(55003)); // The failed instance recovers once.
                await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
                Assert.Equal(missingBirthBefore + 2, packageResolver.CallsFor(55004));

                cache.Prune(processes.Where(static item => item.StartKey is > 0)
                    .Select(static item => new ProcessInstanceKey(item.ProcessId, item.StartKey!.Value))
                    .ToHashSet());
                Assert.Equal(3, cache.Count);
                var retained = new HashSet<ProcessInstanceKey>
                {
                    new(process.ProcessId, process.StartKey!.Value),
                    new(55002, 20)
                };
                cache.Prune(retained);
                Assert.Equal(2, cache.Count);
                Assert.False(cache.Contains(new ProcessInstanceKey(55003, 30)));

                var reusedPid = process with { StartKey = 11 };
                var beforePidReuse = bundle.Observations;
                await CaptureCached(provider, [reusedPid], plan, cache, retainedCatalogs);
                Assert.Equal(beforePidReuse + 1, bundle.Observations);
                var oldInstance = new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value);
                var newInstance = new ProcessInstanceKey(reusedPid.ProcessId, reusedPid.StartKey!.Value);
                Assert.True(cache.Contains(oldInstance));
                Assert.True(cache.Contains(newInstance));
                cache.Prune(new HashSet<ProcessInstanceKey> { newInstance });
                Assert.False(cache.Contains(oldInstance));
                Assert.True(cache.Contains(newInstance));
            }
        }
        finally
        {
            foreach (var reader in retainedCatalogs.Values) reader.Dispose();
        }
    }

    [Fact]
    public async Task CachedDirectMatchesStillRecomputeHelperInheritanceAndCategory()
    {
        var retainedCatalogs = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        try
        {
            const string parentPath = @"D:\Apps\Parent\parent.exe";
            var packageResolver = new CountingPackageIdentityResolver();
            var bundle = new MutableSoftwareIdentityCatalog();
            using var provider = CreateProvider(
                new MutableSoftwareRegistryView([CreateSoftware("software:parent", "Parent", @"D:\Apps\Parent")]),
                windowsGameListSource: new FakeWindowsGameListSource { Paths = [parentPath] },
                packageIdentityResolver: packageResolver, softwareIdentityCatalog: bundle);
            var cache = new DirectAttributionCache();
            var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
            var parent = new RuntimeProcessIdentity(56001, null, "parent", parentPath, false,
                StartKey: 10, UserSid: UserOne);
            var helper = new RuntimeProcessIdentity(56002, parent.ProcessId, "msedgewebview2",
                @"D:\Runtime\msedgewebview2.exe", false, StartKey: 20, UserSid: UserOne);

            var withParent = await CaptureCached(provider, [parent, helper], plan, cache, retainedCatalogs);
            var inherited = withParent.Batch.SoftwareByProcessId[helper.ProcessId];
            Assert.Equal("software:parent", inherited.Id);
            Assert.Equal(SoftwareKinds.Game, inherited.Kind);
            Assert.Equal(parent.ProcessId, withParent.Batch.HelperOwnerByProcessId[helper.ProcessId]);
            var callsAfterInitialCapture = bundle.Observations;

            var withoutParent = await CaptureCached(provider, [helper], plan, cache, retainedCatalogs);
            Assert.Equal(callsAfterInitialCapture, bundle.Observations);
            Assert.Equal(RuntimeAttributionIds.Unattributed,
                withoutParent.Batch.SoftwareByProcessId[helper.ProcessId].Id);
            Assert.Equal(SoftwareKinds.Unattributed,
                withoutParent.Batch.SoftwareByProcessId[helper.ProcessId].Kind);
            Assert.DoesNotContain(helper.ProcessId, withoutParent.Batch.HelperOwnerByProcessId.Keys);
        }
        finally
        {
            foreach (var reader in retainedCatalogs.Values) reader.Dispose();
        }
    }

    [Fact]
    public async Task CurrentIdentitySignalsInvalidateDirectMatchesAndUpdateAttribution()
    {
        var retainedCatalogs = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        try
        {
            var packageResolver = new CountingPackageIdentityResolver
            {
                ObserveResult = static process =>
                {
                    var identity = process.WindowTitle is { } title
                        ? $"title:{title}"
                        : process.WindowApplicationUserModelId is { } windowAppId
                            ? $"window-app:{windowAppId}"
                            : process.ApplicationUserModelId is { } appId
                                ? $"application:{appId}"
                                : null;
                    return identity is null
                        ? RuntimeAttributionObservation.NoMatch
                        : RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
                            $"signal:{identity}", identity, SoftwareKinds.Other, "other", []));
                }
            };
            var bundle = new MutableSoftwareIdentityCatalog();
            using var provider = CreateProvider(
                new MutableSoftwareRegistryView([]), packageIdentityResolver: packageResolver, softwareIdentityCatalog: bundle);
            var cache = new DirectAttributionCache();
            var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
            var trayIdentity = new RuntimeProcessIdentity(56003, null, "app", @"D:\Apps\Identity\app.exe", false,
                ApplicationUserModelId: "app:tray", StartKey: 70, UserSid: UserOne);

            var tray = await CaptureCached(provider, [trayIdentity], plan, cache, retainedCatalogs);
            Assert.Equal(1, bundle.Observations);
            Assert.Equal("signal:application:app:tray", tray.Batch.SoftwareByProcessId[trayIdentity.ProcessId].Id);
            var unchangedTray = await CaptureCached(provider, [trayIdentity], plan, cache, retainedCatalogs);
            Assert.Equal(1, bundle.Observations);
            Assert.Equal(tray.Batch.SoftwareByProcessId, unchangedTray.Batch.SoftwareByProcessId);

            packageResolver.PublishInputChange();
            var enrichedAppId = trayIdentity with { ApplicationUserModelId = "app:full-table" };
            var updatedAppId = await CaptureCached(provider, [enrichedAppId], plan, cache, retainedCatalogs);
            Assert.Equal(2, bundle.Observations);
            Assert.Equal("signal:application:app:full-table",
                updatedAppId.Batch.SoftwareByProcessId[trayIdentity.ProcessId].Id);

            packageResolver.PublishInputChange();
            var withWindowAppId = enrichedAppId with { WindowApplicationUserModelId = "window:full-table" };
            var updatedWindowAppId = await CaptureCached(provider, [withWindowAppId], plan, cache, retainedCatalogs);
            Assert.Equal(3, bundle.Observations);
            Assert.Equal("signal:window-app:window:full-table",
                updatedWindowAppId.Batch.SoftwareByProcessId[trayIdentity.ProcessId].Id);

            packageResolver.PublishInputChange();
            var fullTable = withWindowAppId with { WindowTitle = "Visible title" };
            var withTitle = await CaptureCached(provider, [fullTable], plan, cache, retainedCatalogs);
            Assert.Equal(4, bundle.Observations);
            Assert.Equal("signal:title:Visible title",
                withTitle.Batch.SoftwareByProcessId[trayIdentity.ProcessId].Id);
            await CaptureCached(provider, [fullTable], plan, cache, retainedCatalogs);
            Assert.Equal(4, bundle.Observations);

            packageResolver.PublishInputChange();
            var changedTitle = fullTable with { WindowTitle = "Updated title" };
            var updatedTitle = await CaptureCached(provider, [changedTitle], plan, cache, retainedCatalogs);
            Assert.Equal(5, bundle.Observations);
            Assert.Equal("signal:title:Updated title",
                updatedTitle.Batch.SoftwareByProcessId[trayIdentity.ProcessId].Id);
        }
        finally
        {
            foreach (var reader in retainedCatalogs.Values) reader.Dispose();
        }
    }

    [Fact]
    public async Task RecreatedScopeWithIdenticalVersionStillUsesItsNewCatalogSnapshot()
    {
        var bundle = new MutableSoftwareIdentityCatalog();
        var registry = new MutableSoftwareRegistryView([]);
        var cache = new DirectAttributionCache();
        var process = new RuntimeProcessIdentity(55101, null, "child", @"D:\Tools\Child\child.exe", false,
            StartKey: 100, UserSid: UserOne);
        var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        using var firstProvider = CreateProvider(registry, softwareIdentityCatalog: bundle);
        using var first = await firstProvider.AcquireCatalogAsync(UserOne, default);
        await CaptureCached(firstProvider, [process], plan, cache);
        Assert.Equal(1, bundle.Observations);

        // Scope retirement/recreation can restart Generation at 1 with identical
        // version text, but direct PID/birth bindings are captured anew.
        using var recreatedProvider = CreateProvider(registry, softwareIdentityCatalog: bundle);
        using var recreated = await recreatedProvider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(first.Catalog.Generation, recreated.Catalog.Generation);
        Assert.Equal(first.Catalog.Version, recreated.Catalog.Version);
        Assert.NotSame(first.Catalog, recreated.Catalog);
        await CaptureCached(recreatedProvider, [process], plan, cache);
        Assert.Equal(2, bundle.Observations);
        await CaptureCached(recreatedProvider, [process], plan, cache);
        Assert.Equal(2, bundle.Observations);
    }

    [Fact]
    public async Task RecreatedScopeAtGenerationOneRejectsChangedInstalledAndBundledVersions()
    {
        var cache = new DirectAttributionCache();
        var packageResolver = new CountingPackageIdentityResolver();
        var process = new RuntimeProcessIdentity(55100, null, "app", @"D:\Apps\Host\app.exe", false,
            StartKey: 100, UserSid: UserOne);
        var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());

        using (var firstProvider = CreateProvider(
            new MutableSoftwareRegistryView([CreateSoftware("software:first", "First", @"D:\Apps\Host")]),
            packageIdentityResolver: packageResolver,
            softwareIdentityCatalog: new MutableSoftwareIdentityCatalog { Version = "1.0.0" }))
        {
            await CaptureCached(firstProvider, [process], plan, cache);
            Assert.Equal(1, packageResolver.Calls);
            Assert.Equal(1, cache.GenerationFor(new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value)));
        }

        using (var changedInstalledScopeProvider = CreateProvider(
            new MutableSoftwareRegistryView([CreateSoftware("software:second", "Second", @"D:\Apps\Host")]),
            packageIdentityResolver: packageResolver,
            softwareIdentityCatalog: new MutableSoftwareIdentityCatalog { Version = "1.0.0" }))
        {
            await CaptureCached(changedInstalledScopeProvider, [process], plan, cache);
            Assert.Equal(1, packageResolver.Calls);
            Assert.Equal(1, cache.GenerationFor(new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value)));
        }

        using var installedVersionProvider = CreateProvider(
            new MutableSoftwareRegistryView([CreateSoftware("software:second", "Second", @"D:\Apps\Host")]),
            packageIdentityResolver: packageResolver,
            softwareIdentityCatalog: new MutableSoftwareIdentityCatalog { Version = "1.0.0" });
        using var installedCatalog = await installedVersionProvider.AcquireCatalogAsync(UserOne, default);
        using (var bundledVersionProvider = CreateProvider(
            new MutableSoftwareRegistryView([CreateSoftware("software:second", "Second", @"D:\Apps\Host")]),
            packageIdentityResolver: packageResolver,
            softwareIdentityCatalog: new MutableSoftwareIdentityCatalog { Version = "1.0.1" }))
        {
            using var bundledCatalog = await bundledVersionProvider.AcquireCatalogAsync(UserOne, default);
            Assert.Equal(1, installedCatalog.Catalog.Generation);
            Assert.Equal(1, bundledCatalog.Catalog.Generation);
            Assert.Equal(installedCatalog.Catalog.Version, bundledCatalog.Catalog.Version);

            await CaptureCached(bundledVersionProvider, [process], plan, cache);
            Assert.Equal(1, packageResolver.Calls);
        }
    }

    [Fact]
    public async Task SoftwareSnapshotUsesPublishedInventoryRevision()
    {
        var registry = new MutableSoftwareRegistryView([CreateSoftware("first", "First", @"D:\Apps\Product")]);
        var revisions = new ResourceManager.App.Application.Management.ManagementInventoryRevisionOwner();
        var games = new FakeWindowsGameListSource();
        using var provider = CreateProvider(registry, windowsGameListSource: games, inventoryRevisionOwner: revisions);
        using var first = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(1, games.ReadCount);
        using var steady = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Same(first.Catalog, steady.Catalog);
        Assert.Equal(1, games.ReadCount); // unchanged revision must not refresh registry-backed sources
        registry.SetSoftware([CreateSoftware("second", "Second", @"D:\Apps\Product")]);
        revisions.ObserveSoftwareInventoryContentChanged(); // existing refresh/content-change publication
        using var changed = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.NotSame(first.Catalog, changed.Catalog);
        Assert.Equal(2, games.ReadCount);
        Assert.Equal("second", changed.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\Product\app.exe")).Id);
    }

    [Fact]
    public async Task RegistrationAndBundledCatalogPublicationsRefreshTheSoftwareSnapshot()
    {
        var registry = new MutableSoftwareRegistryView([]);
        var adapted = new MutableAdapterSoftwareRegistry();
        var controlled = new MutableControlledSoftwareRegistry();
        var games = new FakeWindowsGameListSource();
        var bundled = new MutableSoftwareIdentityCatalog();
        using var provider = CreateProvider(registry, adapted, games, controlledRegistry: controlled, softwareIdentityCatalog: bundled);
        using var first = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(1, games.ReadCount);
        adapted.Registrations = [CreateAdapter("adapter:new", "New", @"D:\Apps\New")];
        using var adapterChange = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(2, games.ReadCount);
        controlled.Registrations = [new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "new.exe", ["New"], [@"D:\Apps\New"], [], [], "controlled")];
        using var controlledChange = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(3, games.ReadCount);
        bundled.Version = "2.0.0";
        using var catalogChange = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(4, games.ReadCount);
        using var unchanged = await provider.AcquireCatalogAsync(UserOne, default);
        Assert.Equal(4, games.ReadCount);
        Assert.Same(catalogChange.Catalog, unchanged.Catalog);
    }


    [Fact]
    public async Task SteadyAttributionFrameDoesNotProbeSixHundredFortyPackageIdentities()
    {
        var retainedCatalogs = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        try
        {
            var packages = new CountingPackageIdentityResolver();
            using var provider = CreateProvider(new MutableSoftwareRegistryView([]), packageIdentityResolver: packages);
            var cache = new DirectAttributionCache();
            var processes = Enumerable.Range(60000, 640).Select(pid => new RuntimeProcessIdentity(pid, null, "app",
                @"D:\Apps\Fixture\app.exe", false, StartKey: pid, UserSid: UserOne)).ToArray();
            var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
            await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
            Assert.Equal(640, packages.Calls);
            for (var frame = 0; frame < 3; frame++) await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
            Assert.Equal(640, packages.Calls);
            Assert.Equal(4, packages.VersionReads); // one input version read per batch, independent of process count
            packages.ObserveResult = _ => RuntimeAttributionObservation.Matched(new("package:updated", "Updated package", SoftwareKinds.RuntimePackage, SoftwareDisplayKinds.WindowsApp, []));
            packages.PublishInputChange(); // package registration/source publication, not a per-process probe
            var changed = await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
            Assert.Equal(1280, packages.Calls);
            Assert.All(changed.Batch.SoftwareByProcessId.Values, value => Assert.Equal("package:updated", value.Id));
            await CaptureCached(provider, processes, plan, cache, retainedCatalogs);
            Assert.Equal(1280, packages.Calls);
        }
        finally
        {
            foreach (var reader in retainedCatalogs.Values) reader.Dispose();
        }
    }


    private sealed class TrackingCatalogProvider(IRuntimeProcessAttributionCatalogProvider inner)
        : IRuntimeProcessAttributionCatalogProvider
    {
        internal List<string?> AcquiredScopes { get; } = [];
        internal string? FailScope { get; init; }
        internal int ReturnedLeases;
        public long SoftwareSnapshotGeneration => inner.SoftwareSnapshotGeneration;
        public Task InvalidateSoftwareSnapshotAsync() => inner.InvalidateSoftwareSnapshotAsync();
        public async Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken)
        {
            AcquiredScopes.Add(windowsSid);
            if (windowsSid == FailScope) throw new InvalidOperationException("owner catalog unavailable");
            var lease = await inner.AcquireCatalogAsync(windowsSid, cancellationToken);
            return new(lease.Catalog, () => { lease.Dispose(); ReturnedLeases++; });
        }
    }

    [Fact]
    public async Task InvalidationRefreshesEveryScopeAndRetiredReadersStayValid()
    {
        var registry = new OwnerSoftwareRegistryView();
        using var provider = CreateProvider(registry);
        using var one = await provider.AcquireCatalogAsync(UserOne, default);
        using var two = await provider.AcquireCatalogAsync(UserTwo, default);
        registry.Suffix = ":updated";
        await provider.InvalidateSoftwareSnapshotAsync();
        using var nextOne = await provider.AcquireCatalogAsync(UserOne, default);
        using var nextTwo = await provider.AcquireCatalogAsync(UserTwo, default);
        var process = CreateProcess(@"D:\Apps\SharedProduct\app.exe");
        Assert.Equal("installed:user-one", one.Catalog.Pipeline.Match(process).Id);
        Assert.Equal("installed:user-two", two.Catalog.Pipeline.Match(process).Id);
        Assert.Equal("installed:user-one:updated", nextOne.Catalog.Pipeline.Match(process).Id);
        Assert.Equal("installed:user-two:updated", nextTwo.Catalog.Pipeline.Match(process).Id);
        provider.Dispose();
        Assert.Equal("installed:user-two", two.Catalog.Pipeline.Match(process).Id);
    }

    private sealed class OwnerSoftwareRegistryView : ISoftwareRegistryView
    {
        internal List<string?> ReadScopes { get; } = [];
        internal string Suffix { get; set; } = "";
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("An owner scope is required.");
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(string? windowsSid, CancellationToken cancellationToken,
            IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadScopes.Add(windowsSid);
            var name = windowsSid == UserOne ? "user-one" : windowsSid == UserTwo ? "user-two" : "machine";
            return Task.FromResult<IReadOnlyList<SoftwareRecord>>([CreateSoftware("installed:" + name + Suffix,
                "Same Product", @"D:\Apps\SharedProduct") with
                { Kind = windowsSid == UserOne ? SoftwareKinds.HighPerformance : SoftwareKinds.Other, HasExplicitKind = true }]);
        }
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task ExplicitCategoryChangeInvalidatesTheAttributionCatalog()
    {
        const string path = @"D:\Apps\One\app.exe";
        var record = CreateSoftware("software:one", "One", @"D:\Apps\One");
        var registry = new MutableSoftwareRegistryView([record]);
        using var provider = CreateProvider(registry,
            windowsGameListSource: new FakeWindowsGameListSource { Paths = [path] });
        using var first = await provider.AcquireCatalogAsync(default);
        Assert.Equal(SoftwareKinds.Game, first.Catalog.Pipeline.Match(CreateProcess(path)).Kind);
        registry.SetSoftware([record with { HasExplicitKind = true }]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var updated = await provider.AcquireCatalogAsync(default);
        Assert.NotSame(first.Catalog, updated.Catalog);
        Assert.Equal(SoftwareKinds.Other, updated.Catalog.Pipeline.Match(CreateProcess(path)).Kind);
    }

    [Fact]
    public async Task WindowsGameListRefreshesWithSoftwareSnapshotAndPreservesLeasedFacts()
    {
        const string path = @"E:\Library\SteamLibrary\steamapps\common\Battlefield 6\bf6.exe";
        var games = new FakeWindowsGameListSource();
        using var provider = CreateProvider(new MutableSoftwareRegistryView([]), windowsGameListSource: games);
        var process = new RuntimeProcessIdentity(46210, null, "bf6", path, false, ProductName: "Battlefield 6");
        using var first = await provider.AcquireCatalogAsync(default);
        Assert.Equal(SoftwareKinds.RuntimeProduct, first.Catalog.Pipeline.Match(process).Kind);
        games.Paths = ["", "bf6.exe", @"..\bf6.exe", path.ToUpperInvariant(), path.Replace('\\', '/')];
        using (var cached = await provider.AcquireCatalogAsync(default)) Assert.Same(first.Catalog, cached.Catalog);
        Assert.Equal(1, games.ReadCount);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var refreshed = await provider.AcquireCatalogAsync(default);
        Assert.Equal(2, games.ReadCount);
        Assert.Equal(2, refreshed.Catalog.Generation);
        Assert.Equal(1, refreshed.Catalog.Version.WindowsGames.ItemCount);
        var before = first.Catalog.Pipeline.Match(process);
        var after = refreshed.Catalog.Pipeline.Match(process);
        Assert.Equal(SoftwareKinds.RuntimeProduct, before.Kind);
        Assert.Equal(SoftwareKinds.Game, after.Kind);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Name, after.Name);
        Assert.NotNull(after.CategorySource);
        Assert.Equal(SoftwareKinds.RuntimeProduct, refreshed.Catalog.Pipeline.Match(process with
            { ExecutablePath = @"D:\Other\bf6.exe" }).Kind);

        games.Paths = [path.ToLowerInvariant()];
        await provider.InvalidateSoftwareSnapshotAsync();
        using (var equivalent = await provider.AcquireCatalogAsync(default)) Assert.Same(refreshed.Catalog, equivalent.Catalog);
        games.Paths = [];
        await provider.InvalidateSoftwareSnapshotAsync();
        using var removed = await provider.AcquireCatalogAsync(default);
        Assert.Equal(3, removed.Catalog.Generation);
        Assert.Equal(SoftwareKinds.RuntimeProduct, removed.Catalog.Pipeline.Match(process).Kind);
        Assert.Equal(SoftwareKinds.Game, refreshed.Catalog.Pipeline.Match(process).Kind);
    }

    [Fact]
    public async Task InvalidateSoftwareSnapshotAsync_RefreshesAnOtherwiseLiveSnapshot()
    {
        var registry = new MutableSoftwareRegistryView(
            [CreateSoftware("software:one", "Software One", @"D:\Apps\One")]);
        using var provider = CreateProvider(registry);

        Assert.Equal(0, provider.SoftwareSnapshotGeneration);

        using var first = await provider.AcquireCatalogAsync(CancellationToken.None);
        registry.SetSoftware(
            [CreateSoftware("software:two", "Software Two", @"D:\Apps\Two")]);

        using (var reused = await provider.AcquireCatalogAsync(CancellationToken.None))
            Assert.Same(first.Catalog, reused.Catalog);

        await provider.InvalidateSoftwareSnapshotAsync();
        Assert.Equal(1, provider.SoftwareSnapshotGeneration);
        using var refreshed = await provider.AcquireCatalogAsync(CancellationToken.None);

        Assert.NotSame(first.Catalog, refreshed.Catalog);
        Assert.Equal(2, refreshed.Catalog.Generation);
        Assert.Equal(
            "software:two",
            refreshed.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\Two\app.exe")).Id);
    }

    [Fact]
    public async Task InvalidateSoftwareSnapshotAsync_ClearsAnOverlappingRefresh()
    {
        var registry = new BlockingSoftwareRegistryView(
            [CreateSoftware("software:old", "Old Software", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry);

        var stage = "before-initial-refresh";
        try
        {
            stage = "start-initial-refresh";
            var initialRefresh = provider.AcquireCatalogAsync(CancellationToken.None);
            stage = "await-initial-read-started";
            await registry.ReadStarted;
            registry.SetSoftware(
                [CreateSoftware("software:new", "New Software", @"D:\Apps\New")]);
            stage = "request-invalidation";
            var invalidation = provider.InvalidateSoftwareSnapshotAsync();
            stage = "release-initial-read";
            registry.ReleaseRead();

            stage = "await-initial-catalog";
            using var staleCatalog = await initialRefresh;
            stage = "await-invalidation";
            await invalidation;
            stage = "create-refreshed-catalog";
            using var refreshed = await provider.AcquireCatalogAsync(CancellationToken.None);

            stage = "assert-old-catalog-match";
            Assert.Equal(
                "software:old",
                staleCatalog.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
            stage = "assert-new-catalog-match";
            Assert.Equal(
                "software:new",
                refreshed.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\New\app.exe")).Id);
            stage = "assert-refreshed-generation";
            Assert.Equal(2, refreshed.Catalog.Generation);
        }
        catch (Exception error)
        {
            // Synchronous diagnostics preserve the original failure if a later
            // owned-Job termination prevents xUnit from writing the final TRX.
            Console.Error.WriteLine($"Attribution overlap failed at {stage}: {error}");
            Console.Error.Flush();
            throw;
        }
    }

    [Fact]
    public async Task SoftwareBaseScoresIncludeTheCatalogOnceWithoutProcessOverridesOrRunningState()
    {
        var first = CreateSoftware("software:one", "One", @"D:\Apps\One");
        var second = CreateSoftware("software:two", "Two", @"D:\Apps\Two");
        var registry = new MutableSoftwareRegistryView([first, second]);
        using var provider = CreateProvider(registry);
        using var catalog = await provider.AcquireCatalogAsync(CancellationToken.None);
        var plan = new CompiledBaseScorePlan(
            new Dictionary<string, double> { [first.Id] = 80, [second.Id] = 40 },
            new Dictionary<string, double> { [first.Id + "\nprocess:test"] = 100 });

        var scores = catalog.Catalog.GetSoftwareBaseScores(plan);
        Assert.Equal(100, plan.ResolveBaseScore(first.Id, first.Kind, "process:test"));
        Assert.Equal(new[] { 80D, 40D }, scores.Select(static item => item.BaseScore));
        Assert.Equal(2, scores.Length);
        Assert.Equal(scores, catalog.Catalog.GetSoftwareBaseScores(plan));

        // Process attribution and its runtime fallback never insert software into this catalog.
        catalog.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\One\app.exe"));
        catalog.Catalog.Pipeline.Match(new RuntimeProcessIdentity(101, null, "helper", @"D:\Apps\One\helper.exe", false));
        catalog.Catalog.Pipeline.Match(CreateProcess(@"D:\Unknown\new-process.exe"));
        Assert.Equal(scores, catalog.Catalog.GetSoftwareBaseScores(plan));

        var updated = plan with
        {
            SoftwareBaseScoresBySoftwareId = new Dictionary<string, double>
                { [first.Id] = 20, [second.Id] = 40 }
        };
        Assert.Equal(new[] { 20D, 40D }, catalog.Catalog.GetSoftwareBaseScores(updated)
            .Select(static item => item.BaseScore));
        Assert.Equal(new[] { 80D, 40D }, scores.Select(static item => item.BaseScore));

        catalog.Dispose(); // Return the old read reference before selecting the next directory.

        registry.SetSoftware([second]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var reduced = await provider.AcquireCatalogAsync(CancellationToken.None);
        Assert.Equal(second.Id, Assert.Single(reduced.Catalog.GetSoftwareBaseScores(updated)).SoftwareId);
        reduced.Dispose();
        registry.SetSoftware([]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var empty = await provider.AcquireCatalogAsync(CancellationToken.None);
        Assert.Empty(empty.Catalog.GetSoftwareBaseScores(updated));
    }

    [Fact]
    public async Task ConcurrentReuseAndReplacementKeepRetiredPipelineUntilLastReaderReturns()
    {
        var registry = new MutableSoftwareRegistryView([CreateSoftware("old", "Old", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry);
        using var first = await provider.AcquireCatalogAsync(default);
        var old = first.Catalog;
        var readers = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => provider.AcquireCatalogAsync(default))));
        try
        {
            Assert.All(readers, reader => Assert.Same(old, reader.Catalog));
            registry.SetSoftware([CreateSoftware("next", "Next", @"D:\Apps\Next")]);
            await provider.InvalidateSoftwareSnapshotAsync();
            using var next = await Task.Run(() => provider.AcquireCatalogAsync(default));
            Assert.NotSame(old, next.Catalog);
            Assert.Equal("old", old.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
            first.Dispose();
            foreach (var reader in readers.Take(readers.Length - 1)) reader.Dispose();
            Assert.Equal("old", readers[^1].Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
            readers[^1].Dispose();
            Assert.Throws<ObjectDisposedException>(() => old.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")));
            Assert.Throws<ObjectDisposedException>(() => first.Catalog);
            Assert.Equal("next", next.Catalog.Pipeline.Match(CreateProcess(@"D:\Apps\Next\app.exe")).Id);
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }

    [Fact]
    public async Task RetainedProcessAttributionRunReusesRefreshesAndDrainsItsNativeCatalogs()
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        var registry = new MutableSoftwareRegistryView([CreateSoftware("old", "Old", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry, softwareIdentityOwner: owner);
        var retained = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        var plan = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        RuntimeProcessIdentity[] processes = [CreateProcess(@"D:\Apps\Old\app.exe") with { UserSid = UserOne }];
        RuntimeProcessAttributionPipeline? finalPipeline = null;
        try
        {
            await Capture();
            var original = retained[UserOne].Catalog;
            for (var round = 0; round < 3; round++)
            {
                await Capture();
                Assert.Same(original, retained[UserOne].Catalog);
            }
            using var concurrent = await provider.AcquireCatalogAsync(UserOne, default);
            Assert.Same(original, concurrent.Catalog);
            registry.SetSoftware([CreateSoftware("new", "New", @"D:\Apps\Old")]);
            await provider.InvalidateSoftwareSnapshotAsync();
            var changed = await Capture();
            var replacement = retained[UserOne].Catalog;
            Assert.NotSame(original, replacement);
            Assert.Equal(original.Generation + 1, replacement.Generation);
            Assert.Equal("new", changed.Batch.SoftwareByProcessId[100].Id);
            Assert.Equal("old", concurrent.Catalog.Pipeline.Match(processes[0]).Id);
            concurrent.Dispose();
            Assert.Throws<ObjectDisposedException>(() => original.Pipeline.Match(processes[0]));
            await Capture();
            Assert.Same(replacement, retained[UserOne].Catalog);
            finalPipeline = replacement.Pipeline;
        }
        finally
        {
            foreach (var reader in retained.Values) reader.Dispose();
        }
        Assert.NotNull(finalPipeline);
        Assert.Throws<ObjectDisposedException>(() => finalPipeline.Match(processes[0]));

        Task<RuntimeProcessAttributionCaptureResult> Capture() => RuntimeProcessAttributionCapture.CaptureAsync(
            provider, processes, plan, default, retainedCatalogs: retained);
    }

    [Fact]
    public async Task ProcessAttributionCatalogBuildCostWith480InstalledSoftwareRecords()
    {
        var software = Enumerable.Range(0, 480).Select(index => CreateSoftware(
            $"installed:benchmark-{index}", $"Installed Product {index}", $@"D:\Apps\Product{index}")).ToArray();
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var provider = CreateProvider(new MutableSoftwareRegistryView(software), softwareIdentityOwner: owner);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var reader = await provider.AcquireCatalogAsync(UserOne, default);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        Assert.Equal("installed:benchmark-479", reader.Catalog.Pipeline.Match(
            CreateProcess(@"D:\Apps\Product479\app.exe")).Id);
        var firstCatalog = reader.Catalog;
        reader.Dispose();
        cpuBefore = process.TotalProcessorTime;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var rebuilt = await provider.AcquireCatalogAsync(UserOne, default);
        var rebuildElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var rebuildCpu = process.TotalProcessorTime - cpuBefore;
        Assert.NotSame(firstCatalog, rebuilt.Catalog);
        Assert.Equal("installed:benchmark-479", rebuilt.Catalog.Pipeline.Match(
            CreateProcess(@"D:\Apps\Product479\app.exe")).Id);
        var measurement = System.Text.Json.JsonSerializer.Serialize(new
        {
            softwareRecords = software.Length,
            coldWallMilliseconds = elapsed.TotalMilliseconds,
            coldProcessCpuMilliseconds = cpu.TotalMilliseconds,
            rebuildWallMilliseconds = rebuildElapsed.TotalMilliseconds,
            rebuildProcessCpuMilliseconds = rebuildCpu.TotalMilliseconds,
            includes = "each acquire has no live holder and builds four native source catalogs and the resolution owner workspace"
        });
        Console.WriteLine(measurement);
        if (Environment.GetEnvironmentVariable("RM_ATTRIBUTION_BENCHMARK_OUTPUT") is { Length: > 0 } output)
            await File.WriteAllTextAsync(output, measurement);
    }

    [Fact]
    public async Task LastReaderClosesNativeCatalogsAndResolutionWithoutAnotherAcquire()
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var provider = CreateProvider(new MutableSoftwareRegistryView(
            [CreateSoftware("old", "Old", @"D:\Apps\Old")]), softwareIdentityOwner: owner);
        using var first = await provider.AcquireCatalogAsync(default);
        using var last = await provider.AcquireCatalogAsync(default);
        var pipeline = first.Catalog.Pipeline;
        var resolver = ReadField<NativeRuntimeProcessAttributionResolver>(pipeline, "resolver");
        var catalogs = new[] { "adaptedCatalog", "controlledCatalog", "selfCatalog", "knownCatalog" }
            .Select(name => ReadField<NativeSoftwareIdentityCatalogWorkspace>(
                ReadField<NativeRuntimeSourceCatalog>(resolver, name), "workspace").Session).ToArray();
        var resolution = ReadField<HostManagerSoftwareIdentityOwner.SessionLease>(resolver, "lease").ResolutionSession;
        first.Dispose();
        Assert.All(catalogs, session => Assert.NotEqual(0UL, session.Capacity.ResidentByteCount));
        Assert.NotEqual(0UL, resolution.Capacity.ResidentByteCount);
        last.Dispose();
        AssertClosed(); // release is synchronous at the existing reader boundary
        await Task.Delay(TimeSpan.FromSeconds(8.1));
        AssertClosed(); // the provider remains alive, with no later acquire or tick

        void AssertClosed()
        {
            Assert.Throws<ObjectDisposedException>(() => pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")));
            Assert.All(catalogs, session => Assert.Throws<ObjectDisposedException>(() => session.Capacity));
            Assert.Throws<ObjectDisposedException>(() => resolution.Capacity);
        }
    }

    private static T ReadField<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(instance)!;

    [Fact]
    public async Task ReplacementWithoutReadersDisposesThePriorPipelineBeforeReturning()
    {
        var registry = new MutableSoftwareRegistryView([CreateSoftware("old", "Old", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry);
        var first = await provider.AcquireCatalogAsync(default);
        var oldPipeline = first.Catalog.Pipeline;
        first.Dispose();
        registry.SetSoftware([]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var next = await provider.AcquireCatalogAsync(default);
        Assert.Throws<ObjectDisposedException>(() => oldPipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")));
        Assert.Equal(2, next.Catalog.Generation);
    }

    [Fact]
    public async Task ProviderDisposeRejectsAcquisitionButAnExistingReaderCanFinish()
    {
        var registry = new MutableSoftwareRegistryView([CreateSoftware("old", "Old", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry);
        using var reader = await provider.AcquireCatalogAsync(default);
        var pipeline = reader.Catalog.Pipeline;
        provider.Dispose();
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.AcquireCatalogAsync(default));
        Assert.Equal("old", pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")));
    }

    [Fact]
    public async Task FailedCatalogCreationKeepsTheOldDirectoryAndItsGeneration()
    {
        var original = CreateSoftware("old", "Old", @"D:\Apps\Old");
        var registry = new MutableSoftwareRegistryView([original]);
        using var provider = CreateProvider(registry);
        using var reader = await provider.AcquireCatalogAsync(default);
        var old = reader.Catalog;
        // Duplicate real software identities fail construction, rather than failing a registry read.
        registry.SetSoftware([original, original]);
        await provider.InvalidateSoftwareSnapshotAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.AcquireCatalogAsync(default));
        Assert.Equal("old", old.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
        registry.SetSoftware([original]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var recovered = await provider.AcquireCatalogAsync(default);
        Assert.Same(old, recovered.Catalog);
        Assert.Equal(1, recovered.Catalog.Generation);
    }

    [Fact]
    public async Task CancellationAfterInputsDoesNotLeakAnUnreturnedReaderReference()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelOnRead = false;
        var registry = new MutableSoftwareRegistryView([CreateSoftware("old", "Old", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry, new EmptyAdapterSoftwareRegistry(() =>
        {
            if (cancelOnRead) cancellation.Cancel();
        }));
        using var first = await provider.AcquireCatalogAsync(default);
        var oldPipeline = first.Catalog.Pipeline;
        cancelOnRead = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AcquireCatalogAsync(cancellation.Token));
        cancelOnRead = false;
        first.Dispose();
        registry.SetSoftware([]);
        await provider.InvalidateSoftwareSnapshotAsync();
        using var next = await provider.AcquireCatalogAsync(default);
        Assert.Throws<ObjectDisposedException>(() => oldPipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")));
        Assert.Equal(2, next.Catalog.Generation);
    }

    internal static RuntimeProcessAttributionCatalogProvider CreateProvider(
        ISoftwareRegistryView softwareRegistry,
        IAdapterSoftwareRegistry? adapterRegistry = null,
        IWindowsGameListSource? windowsGameListSource = null,
        IRuntimePackageIdentityResolver? packageIdentityResolver = null,
        IControlledSoftwareRegistry? controlledRegistry = null,
        ISoftwareIdentityCatalog? softwareIdentityCatalog = null,
        HostManagerSoftwareIdentityOwner? softwareIdentityOwner = null,
        IRuntimeServiceIdentityResolver? serviceIdentityResolver = null,
        IRuntimeRootIdentityResolver? rootIdentityResolver = null,
        IRuntimeSystemProcessClassifier? systemClassifier = null,
        ResourceManager.App.Application.Management.IManagementInventoryRevisionOwner? inventoryRevisionOwner = null)
    {
        var resolvers = new NullResolvers();
        return new RuntimeProcessAttributionCatalogProvider(
            softwareRegistry,
            adapterRegistry ?? new EmptyAdapterSoftwareRegistry(),
            controlledRegistry ?? new EmptyControlledSoftwareRegistry(),
            packageIdentityResolver ?? resolvers,
            serviceIdentityResolver ?? resolvers,
            rootIdentityResolver ?? resolvers,
            systemClassifier ?? resolvers,
            softwareIdentityCatalog ?? SoftwareIdentityCatalogTestData.Empty,
            softwareIdentityOwner ?? SoftwareIdentityCatalogTestData.Owner,
            windowsGameListSource ?? new FakeWindowsGameListSource(), inventoryRevisionOwner);
    }

    private sealed class MutableSoftwareIdentityCatalog : ISoftwareIdentityCatalog,
        INativeSoftwareIdentityProcessObserver
    {
        private readonly SoftwareIdentityCatalogCompiler inner = SoftwareIdentityCatalogTestData.Empty;

        public string Version { get; set; } = "1.0.0";
        internal int Observations { get; private set; }
        public IReadOnlyList<SoftwareIdentityCatalogEntry> Entries => inner.Entries;
        public SoftwareIdentityCatalogEntry? MatchInstalledSoftware(InstalledSoftwareEntry software)
            => inner.MatchInstalledSoftware(software);
        public SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process)
            => inner.MatchProcess(process);
        public PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process)
            => inner.MatchPortableProcess(process);
        NativeCatalogProcessObservation INativeSoftwareIdentityProcessObserver.ObserveProcess(
            RuntimeProcessIdentity process)
        {
            Observations++;
            return ((INativeSoftwareIdentityProcessObserver)inner).ObserveProcess(process);
        }
    }

    private sealed class DirectAttributionCache
    {
        private readonly ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution> entries = new();

        internal int Count => entries.Count;

        internal bool Contains(ProcessInstanceKey key) => entries.TryGet(key, out _);

        internal long GenerationFor(ProcessInstanceKey key)
            => entries.TryGet(key, out var cached) && cached is not null
                ? cached.CatalogGeneration
                : throw new InvalidOperationException("The process instance has no attribution cache entry.");

        internal RuntimeProcessAttributionDirectMatch? TryGet(
            RuntimeProcessIdentity process,
            string scope,
            RuntimeProcessAttributionCatalog catalog)
            => WindowsResourceBreakdownSampler.TryGetCachedDirectMatch(entries, process, scope, catalog);

        internal void Set(
            RuntimeProcessIdentity process,
            string scope,
            RuntimeProcessAttributionCatalog catalog,
            RuntimeProcessAttributionDirectMatch match)
            => WindowsResourceBreakdownSampler.CacheDirectMatch(entries, process, scope, catalog, match);

        internal RuntimeProcessAttributionFixedInputs? GetFixedInputs(RuntimeProcessIdentity process)
            => process.StartKey is > 0 && entries.TryGet(new(process.ProcessId, process.StartKey.Value), out var entry)
                ? entry?.FixedInputs : null;

        internal void Prune(IReadOnlySet<ProcessInstanceKey> liveInstances) => entries.Prune(liveInstances);
    }

    private sealed class CountingPackageIdentityResolver : IRuntimePackageIdentityResolver
    {
        private int calls;
        private object version = new();
        internal int VersionReads;
        public object InputVersion { get { VersionReads++; return version; } }
        internal void PublishInputChange() => version = new();
        private readonly Dictionary<int, int> callsByPid = [];

        internal int Calls => Volatile.Read(ref calls);
        internal int CallsFor(int pid) => callsByPid.GetValueOrDefault(pid);
        internal int? FailProcessId { get; set; }
        internal Func<RuntimeProcessIdentity, RuntimeAttributionObservation>? ObserveResult { get; set; }

        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
        {
            Interlocked.Increment(ref calls);
            callsByPid[process.ProcessId] = CallsFor(process.ProcessId) + 1;
            if (process.ProcessId == FailProcessId) throw new IOException("test package observation failure");
            return ObserveResult?.Invoke(process) ?? RuntimeAttributionObservation.NoMatch;
        }
    }

    private sealed class MutableAdapterSoftwareRegistry : IAdapterSoftwareRegistry
    {
        internal IReadOnlyList<AdapterSoftwareRegistration> Registrations { get; set; } = [];

        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Registrations);
        }

        public Task<AdapterRegistrationResult> RegisterAsync(AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class MutableControlledSoftwareRegistry : IControlledSoftwareRegistry
    {
        internal IReadOnlyList<ControlledSoftwareRegistration> Registrations { get; set; } = [];

        public IReadOnlyList<ControlledSoftwareRegistration> GetAll() => Registrations;
        public ControlledSoftwareRegistration Register(ControlledSoftwareRegistrationRequest request)
            => throw new NotSupportedException();
        public bool Remove(Guid id) => throw new NotSupportedException();
    }

    private static async Task<RuntimeProcessAttributionCaptureResult> CaptureCached(
        IRuntimeProcessAttributionCatalogProvider provider,
        IReadOnlyList<RuntimeProcessIdentity> processes,
        CompiledBaseScorePlan plan,
        DirectAttributionCache cache,
        IDictionary<string, RuntimeProcessAttributionCatalogLease>? retainedCatalogs = null)
        => await RuntimeProcessAttributionCapture.CaptureAsync(provider, processes, plan, default,
            cache.TryGet, cache.Set, cache.GetFixedInputs, retainedCatalogs);

    private static (HostManagerSoftwareIdentityOwner Owner, RuntimePlanProvider PlanProvider)
        CreateMutableSoftwareIdentityOwner()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "attribution-cache-test",
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        var owner = new HostManagerSoftwareIdentityOwner(provider,
            new HostManagerSoftwareIdentityRuntime(provider, deployment),
            NullLogger<HostManagerSoftwareIdentityOwner>.Instance);
        return (owner, provider);
    }

    private static void PublishSoftwareIdentityPlan(
        RuntimePlanProvider provider,
        long version,
        Action<JsonObject> editProfile)
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan(editProfile, planEpoch: checked((ulong)version));
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "attribution-cache-test",
            HostManager = hostPlan
        });
    }

    private static AdapterSoftwareRegistration CreateAdapter(string id, string displayName, string rootPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdapterSoftwareRegistration(id, AdapterRegistrationSchemaVersions.Current,
            $"{id}.adapter", $"{id}.app", displayName, null, [rootPath], [], [],
            new AdapterResourceMarkerEndpoint(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:9000/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, now, 200, "ok"),
            "registered", now, now);
    }

    private sealed class FakeWindowsGameListSource : IWindowsGameListSource
    {
        public IReadOnlyList<string> Paths { get; set; } = [];
        public int ReadCount { get; private set; }
        public IReadOnlyList<string> ReadExecutablePaths()
        {
            ReadCount++;
            return Paths;
        }
    }

    private static SoftwareRecord CreateSoftware(
        string id,
        string name,
        string rootPath)
    {
        return new SoftwareRecord(
            id,
            name,
            SoftwareKinds.Other,
            "manual",
            [],
            [rootPath],
            new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"),
            null);
    }

    private static RuntimeProcessIdentity CreateProcess(string executablePath)
        => new(100, null, "app", executablePath, false);

    private class MutableSoftwareRegistryView(
        IReadOnlyList<SoftwareRecord> software) : ISoftwareRegistryView
    {
        private readonly object gate = new();
        private IReadOnlyList<SoftwareRecord> current = software;

        public virtual Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return Task.FromResult(current);
            }
        }

        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(
            CancellationToken cancellationToken)
            => GetSoftwareAsync(cancellationToken);

        public void SetSoftware(IReadOnlyList<SoftwareRecord> next)
        {
            lock (gate)
            {
                current = next;
            }
        }
    }

    private sealed class BlockingSoftwareRegistryView(
        IReadOnlyList<SoftwareRecord> software) : MutableSoftwareRegistryView(software)
    {
        private readonly TaskCompletionSource readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int shouldBlock = 1;

        internal Task ReadStarted => readStarted.Task;

        public override async Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(
            CancellationToken cancellationToken)
        {
            var snapshot = await base.GetSoftwareAsync(cancellationToken);
            if (Interlocked.Exchange(ref shouldBlock, 0) != 0)
            {
                readStarted.TrySetResult();
                await releaseRead.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        internal void ReleaseRead()
            => releaseRead.TrySetResult();
    }

    private sealed class EmptyAdapterSoftwareRegistry(Action? afterRead = null) : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(
            CancellationToken cancellationToken)
        {
            afterRead?.Invoke();
            return Task.FromResult<IReadOnlyList<AdapterSoftwareRegistration>>([]);
        }

        public Task<AdapterRegistrationResult> RegisterAsync(
            AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class EmptyControlledSoftwareRegistry : IControlledSoftwareRegistry
    {
        public IReadOnlyList<ControlledSoftwareRegistration> GetAll() => [];

        public ControlledSoftwareRegistration Register(
            ControlledSoftwareRegistrationRequest request)
            => throw new NotSupportedException();

        public bool Remove(Guid id)
            => throw new NotSupportedException();
    }

    private sealed class NullResolvers :
        IRuntimePackageIdentityResolver,
        IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver,
        IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
