using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class RuntimeProcessAttributionCatalogProviderTests
{
    [Fact]
    public async Task InvalidateSoftwareSnapshotAsync_RefreshesAnOtherwiseLiveSnapshot()
    {
        var registry = new MutableSoftwareRegistryView(
            [CreateSoftware("software:one", "Software One", @"D:\Apps\One")]);
        using var provider = CreateProvider(registry);

        Assert.Equal(0, provider.SoftwareSnapshotGeneration);

        var first = await provider.GetCatalogAsync(CancellationToken.None);
        registry.SetSoftware(
            [CreateSoftware("software:two", "Software Two", @"D:\Apps\Two")]);

        Assert.Same(first, await provider.GetCatalogAsync(CancellationToken.None));

        await provider.InvalidateSoftwareSnapshotAsync();
        Assert.Equal(1, provider.SoftwareSnapshotGeneration);
        var refreshed = await provider.GetCatalogAsync(CancellationToken.None);

        Assert.NotSame(first, refreshed);
        Assert.Equal(2, refreshed.Generation);
        Assert.Equal(
            "software:two",
            refreshed.Pipeline.Match(CreateProcess(@"D:\Apps\Two\app.exe")).Id);
    }

    [Fact]
    public async Task InvalidateSoftwareSnapshotAsync_ClearsAnOverlappingRefresh()
    {
        var registry = new BlockingSoftwareRegistryView(
            [CreateSoftware("software:old", "Old Software", @"D:\Apps\Old")]);
        using var provider = CreateProvider(registry);

        var initialRefresh = provider.GetCatalogAsync(CancellationToken.None);
        await registry.ReadStarted;
        registry.SetSoftware(
            [CreateSoftware("software:new", "New Software", @"D:\Apps\New")]);
        var invalidation = provider.InvalidateSoftwareSnapshotAsync();
        registry.ReleaseRead();

        var staleCatalog = await initialRefresh;
        await invalidation;
        var refreshed = await provider.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(
            "software:old",
            staleCatalog.Pipeline.Match(CreateProcess(@"D:\Apps\Old\app.exe")).Id);
        Assert.Equal(
            "software:new",
            refreshed.Pipeline.Match(CreateProcess(@"D:\Apps\New\app.exe")).Id);
        Assert.Equal(2, refreshed.Generation);
    }

    [Fact]
    public async Task SoftwareBaseScoresIncludeTheCatalogOnceWithoutProcessOverridesOrRunningState()
    {
        var first = CreateSoftware("software:one", "One", @"D:\Apps\One");
        var second = CreateSoftware("software:two", "Two", @"D:\Apps\Two");
        var registry = new MutableSoftwareRegistryView([first, second]);
        using var provider = CreateProvider(registry);
        var catalog = await provider.GetCatalogAsync(CancellationToken.None);
        var plan = new CompiledBaseScorePlan(
            new Dictionary<string, double> { [first.Id] = 80, [second.Id] = 40 },
            new Dictionary<string, double> { [first.Id + "\nprocess:test"] = 100 });

        var scores = catalog.GetSoftwareBaseScores(plan);
        Assert.Equal(100, plan.ResolveBaseScore(first.Id, first.Kind, "process:test"));
        Assert.Equal(new[] { 80D, 40D }, scores.Select(static item => item.BaseScore));
        Assert.Equal(2, scores.Length);
        Assert.Equal(scores, catalog.GetSoftwareBaseScores(plan));

        // Process attribution and its runtime fallback never insert software into this catalog.
        catalog.Pipeline.Match(CreateProcess(@"D:\Apps\One\app.exe"));
        catalog.Pipeline.Match(new RuntimeProcessIdentity(101, null, "helper", @"D:\Apps\One\helper.exe", false));
        catalog.Pipeline.Match(CreateProcess(@"D:\Unknown\new-process.exe"));
        Assert.Equal(scores, catalog.GetSoftwareBaseScores(plan));

        var updated = plan with
        {
            SoftwareBaseScoresBySoftwareId = new Dictionary<string, double>
                { [first.Id] = 20, [second.Id] = 40 }
        };
        Assert.Equal(new[] { 20D, 40D }, catalog.GetSoftwareBaseScores(updated)
            .Select(static item => item.BaseScore));
        Assert.Equal(new[] { 80D, 40D }, scores.Select(static item => item.BaseScore));

        registry.SetSoftware([second]);
        await provider.InvalidateSoftwareSnapshotAsync();
        var reduced = await provider.GetCatalogAsync(CancellationToken.None);
        Assert.Equal(second.Id, Assert.Single(reduced.GetSoftwareBaseScores(updated)).SoftwareId);
        registry.SetSoftware([]);
        await provider.InvalidateSoftwareSnapshotAsync();
        Assert.Empty((await provider.GetCatalogAsync(CancellationToken.None)).GetSoftwareBaseScores(updated));
    }

    private static RuntimeProcessAttributionCatalogProvider CreateProvider(
        ISoftwareRegistryView softwareRegistry)
    {
        var resolvers = new NullResolvers();
        return new RuntimeProcessAttributionCatalogProvider(
            softwareRegistry,
            new EmptyAdapterSoftwareRegistry(),
            new EmptyControlledSoftwareRegistry(),
            resolvers,
            resolvers,
            resolvers,
            resolvers,
            SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner);
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
            "其他软件",
            "manual",
            ["test"],
            [rootPath],
            "",
            new SoftwareOperationCapabilities(false, "None", "不可卸载", ""),
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

    private sealed class EmptyAdapterSoftwareRegistry : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AdapterSoftwareRegistration>>([]);

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
