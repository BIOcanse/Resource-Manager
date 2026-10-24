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
        const string path = @"F:\SteamLibrary\steamapps\common\Battlefield 6\bf6.exe";
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

    private static RuntimeProcessAttributionCatalogProvider CreateProvider(
        ISoftwareRegistryView softwareRegistry,
        IAdapterSoftwareRegistry? adapterRegistry = null,
        IWindowsGameListSource? windowsGameListSource = null)
    {
        var resolvers = new NullResolvers();
        return new RuntimeProcessAttributionCatalogProvider(
            softwareRegistry,
            adapterRegistry ?? new EmptyAdapterSoftwareRegistry(),
            new EmptyControlledSoftwareRegistry(),
            resolvers,
            resolvers,
            resolvers,
            resolvers,
            SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner,
            windowsGameListSource ?? new FakeWindowsGameListSource());
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
            "other",
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
