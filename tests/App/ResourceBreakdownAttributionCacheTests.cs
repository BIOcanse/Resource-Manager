using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Services;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class ResourceBreakdownAttributionCacheTests
{
    private const string Sid = "S-1-5-21-101-102-103-1001";
    private static readonly CompiledBaseScorePlan Scores = new(new Dictionary<string, double>(), new Dictionary<string, double>());
    private static readonly RuntimeProcessIdentity Child = new(57001, 57000, "child", @"D:\Tools\Child\child.exe",
        false, StartKey: 100, UserSid: Sid);

    [Fact]
    public async Task ServiceMembershipAndDisplayNameFollowTheServiceOwnersSnapshot()
    {
        var clock = new Clock();
        IReadOnlyList<WindowsServiceRecord>? entries =
            [Service("SvcA", "Service A"), Service("SvcB", "Service B") with { ProcessId = 0, State = 1 }];
        var reads = 0;
        var services = new WindowsServiceIdentityResolver(() => { reads++; return entries; }, _ => 100, clock);
        using var provider = Provider(service: services, system: new WindowsSystemProcessClassifier());
        var cache = new Cache();
        var process = Child with { Name = "svchost", ExecutablePath = @"C:\Windows\System32\svchost.exe" };
        var first = await Capture(provider, cache, process);
        Assert.Equal("windows-service:svca", first.Id);
        var originalInputs = cache.Entry(process).DirectMatch!.Value.ExternalInputs;
        Assert.Equal(first, await Capture(provider, cache, process));
        Assert.Same(originalInputs, cache.Entry(process).DirectMatch!.Value.ExternalInputs);
        Assert.Equal(1, reads);

        entries = [Service("SvcA", "Service A"), Service("SvcB", "Service B")];
        Assert.Equal(first, await Capture(provider, cache, process)); // Existing snapshot has not expired.
        clock.Advance();
        Assert.Equal("windows-service-group:svca-svcb-2", (await Capture(provider, cache, process)).Id);
        Assert.Equal("windows-service-group:svca-svcb-2",
            cache.Entry(process).DirectMatch!.Value.ExternalInputs!.Service.Attribution!.Id);
        Assert.Equal(2, reads);
        Assert.Equal(1, cache.Entry(process).CatalogGeneration);

        entries = [Service("SvcA", "Renamed A")];
        clock.Advance();
        Assert.Equal("Renamed A", (await Capture(provider, cache, process)).Name);
        entries = [Service("SvcA", "Renamed Again")];
        clock.Advance();
        Assert.Equal("Renamed Again", (await Capture(provider, cache, process)).Name);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++) _ = services.Observe(process);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task ServiceNoMatchUnavailableRecoveryAndBirthVerificationAreNotFrozen()
    {
        var clock = new Clock();
        IReadOnlyList<WindowsServiceRecord>? entries = [];
        long? birth = 100;
        var services = new WindowsServiceIdentityResolver(() => entries, _ => birth, clock);
        using var provider = Provider(service: services);
        var cache = new Cache();
        Assert.Equal(RuntimeAttributionIds.Unattributed, (await Capture(provider, cache, Child)).Id);
        entries = [Service("SvcA", "A")];
        clock.Advance();
        Assert.Equal("windows-service:svca", (await Capture(provider, cache, Child)).Id);
        entries = null;
        clock.Advance();
        Assert.Equal(await Uncached(provider, Child), await Capture(provider, cache, Child));
        entries = [Service("SvcB", "B")];
        clock.Advance();
        Assert.Equal("windows-service:svcb", (await Capture(provider, cache, Child)).Id);
        birth = 101;
        clock.Advance();
        Assert.NotEqual("windows-service:svcb", (await Capture(provider, cache, Child)).Id);
        Assert.Equal("windows-service:svcb", (await Capture(provider, cache, Child with { StartKey = 101 })).Id);
    }

    [Fact]
    public async Task CommandLineAndAncestryRecoveryClearsTheOlderDirectMatch()
    {
        using var provider = Provider(root: new WindowsRuntimeRootIdentityResolver());
        var cache = new Cache();
        cache.Prepare(Child);
        Assert.Equal("runtime-root:d-tools-child", (await Capture(provider, cache, Child)).Id);
        Assert.NotNull(cache.Entry(Child).DirectMatch);
        var recovered = Child with { IsSelfDescendant = true, CommandLine = "child.exe --owned" };
        cache.Prepare(recovered); // Same production helper used after the command-line read recovers.
        Assert.Null(cache.Entry(recovered).DirectMatch);
        Assert.Null(cache.Entry(recovered).CatalogVersion);
        Assert.Equal(0, cache.Entry(recovered).CatalogGeneration);
        Assert.Equal(RuntimeAttributionIds.ResourceManagerSelf, (await Capture(provider, cache, recovered)).Id);
        var warm = cache.Entry(recovered);
        cache.Prepare(recovered);
        Assert.Same(warm, cache.Entry(recovered));
    }

    public static TheoryData<string, RuntimeProcessIdentity> ChangedIdentities => new()
    {
        { "pid", Child with { ProcessId = 57002 } },
        { "parent", Child with { ParentProcessId = 57003 } },
        { "name", Child with { Name = "other" } },
        { "path", Child with { ExecutablePath = @"D:\Tools\Other\child.exe" } },
        { "ancestry", Child with { IsSelfDescendant = true } },
        { "description", Child with { FileDescription = "Recovered description" } },
        { "product", Child with { ProductName = "Recovered product" } },
        { "company", Child with { CompanyName = "Recovered company" } },
        { "app-id", Child with { ApplicationUserModelId = "Recovered.App" } },
        { "window-app-id", Child with { WindowApplicationUserModelId = "Recovered.Window" } },
        { "title", Child with { WindowTitle = "Recovered title" } },
        { "birth", Child with { StartKey = 101 } },
        { "command", Child with { CommandLine = "child.exe --recovered" } },
        { "sid", Child with { UserSid = "S-1-5-21-101-102-103-1002" } }
    };

    [Theory]
    [MemberData(nameof(ChangedIdentities))]
    public async Task EveryChangedIdentityFieldDropsTheMatch(string field, RuntimeProcessIdentity changed)
    {
        _ = field;
        using var provider = Provider();
        var cache = new Cache();
        await Capture(provider, cache, Child);
        cache.Prepare(changed);
        Assert.Null(cache.Entry(changed).DirectMatch);
        await Capture(provider, cache, changed);
        Assert.Equal(changed, cache.Entry(changed).Identity);
        Assert.NotNull(cache.Entry(changed).DirectMatch);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("service")]
    [InlineData("root")]
    [InlineData("root-app-data")]
    [InlineData("system")]
    public async Task ExternalObservationsInvalidateEvenWhenIdentityAndCatalogAreUnchanged(string source)
    {
        var mutable = new MutableResolver();
        using var provider = Provider(package: source == "package" ? mutable : null,
            service: source == "service" ? mutable : null,
            root: source.StartsWith("root", StringComparison.Ordinal) ? mutable : null,
            system: source == "system" ? mutable : null);
        var cache = new Cache();
        var process = source == "root-app-data"
            ? Child with { ExecutablePath = @"C:\Users\Fixture\AppData\Local\Product\child.exe" }
            : Child;
        await Capture(provider, cache, process);
        mutable.Current = RuntimeAttributionObservation.Unavailable;
        Assert.Equal(await Uncached(provider, process), await Capture(provider, cache, process));
        var kind = source switch
        {
            "package" => SoftwareKinds.RuntimePackage, "service" => SoftwareKinds.WindowsService,
            "root" or "root-app-data" => SoftwareKinds.RuntimeRoot, _ => SoftwareKinds.WindowsSystem
        };
        var value = new RuntimeSoftwareAttribution(source + ":identity", "Initial", kind, "general", [@"D:\Tools\Child"]);
        mutable.Current = RuntimeAttributionObservation.Matched(value);
        Assert.Equal(value, await Capture(provider, cache, process));
        var firstInputs = cache.Entry(process).DirectMatch!.Value.ExternalInputs;
        Assert.Equal(value, await Capture(provider, cache, process));
        Assert.Same(firstInputs, cache.Entry(process).DirectMatch!.Value.ExternalInputs);
        value = value with { RootPaths = [@"D:\TOOLS\Child"] };
        mutable.Current = RuntimeAttributionObservation.Matched(value);
        Assert.Equal(value, await Capture(provider, cache, process));
        value = value with { Name = "Updated" };
        mutable.Current = RuntimeAttributionObservation.Matched(value);
        Assert.Equal(value, await Capture(provider, cache, process));
        value = value with { CategorySource = "changed" };
        mutable.Current = RuntimeAttributionObservation.Matched(value);
        Assert.Equal(value, await Capture(provider, cache, process));
        mutable.Current = RuntimeAttributionObservation.NoMatch;
        Assert.Equal(await Uncached(provider, process), await Capture(provider, cache, process));
    }

    [Fact]
    public async Task ValidationFailureIsReportedForThatProcessInsteadOfReturningTheCachedValue()
    {
        var mutable = new MutableResolver();
        using var provider = Provider(package: mutable);
        var cache = new Cache();
        await Capture(provider, cache, Child);
        mutable.Throw = true;
        var result = await RuntimeProcessAttributionCapture.CaptureAsync(provider, [Child], Scores, default,
            cache.Get, cache.Set);
        Assert.Equal("IOException", result.Batch.Failures[Child.ProcessId]);
        Assert.Empty(result.Batch.SoftwareByProcessId);
    }

    [Fact]
    public async Task StoredMatchKeepsTheServiceSnapshotActuallyUsedByItsResolution()
    {
        var clock = new Clock();
        IReadOnlyList<WindowsServiceRecord> entries = [Service("SvcA", "A")];
        var services = new WindowsServiceIdentityResolver(() => entries, _ => 100, clock);
        using var provider = Provider(service: services);
        var cache = new Cache();
        var result = await RuntimeProcessAttributionCapture.CaptureAsync(provider, [Child], Scores, default,
            cache.Get, (process, scope, catalog, match) =>
            {
                entries = [Service("SvcA", "A"), Service("SvcB", "B")];
                clock.Advance();
                cache.Set(process, scope, catalog, match);
            });
        Assert.Equal("windows-service:svca", result.Batch.SoftwareByProcessId[Child.ProcessId].Id);
        Assert.Equal("windows-service:svca", cache.Entry(Child).DirectMatch!.Value.ExternalInputs!.Service.Attribution!.Id);
        Assert.Equal("windows-service-group:svca-svcb-2", (await Capture(provider, cache, Child)).Id);
    }

    [Fact]
    public async Task EnvironmentDependentPathsReResolveWithoutAnAmbientStateCache()
    {
        const string variable = "RESOURCE_MANAGER_ATTRIBUTION_CACHE_2B";
        var prior = Environment.GetEnvironmentVariable(variable);
        try
        {
            using var provider = Provider(root: new WindowsRuntimeRootIdentityResolver());
            var cache = new Cache();
            var process = Child with { ExecutablePath = "%" + variable + @"%\child.exe" };
            Environment.SetEnvironmentVariable(variable, @"D:\Tools\First");
            Assert.Equal("runtime-root:d-tools-first", (await Capture(provider, cache, process)).Id);
            Assert.Null(cache.Entry(process).DirectMatch);
            Environment.SetEnvironmentVariable(variable, @"D:\Tools\Second");
            Assert.Equal("runtime-root:d-tools-second", (await Capture(provider, cache, process)).Id);
            process = process with { ExecutablePath = @"child.exe" };
            await Capture(provider, cache, process);
            await Capture(provider, cache, process);
            Assert.Null(cache.Entry(process).DirectMatch);
        }
        finally { Environment.SetEnvironmentVariable(variable, prior); }
    }

    private static WindowsServiceRecord Service(string name, string displayName) => new(name, displayName, 0x20, 4, 57001, null);
    private static RuntimeProcessAttributionCatalogProvider Provider(IRuntimePackageIdentityResolver? package = null,
        IRuntimeServiceIdentityResolver? service = null, IRuntimeRootIdentityResolver? root = null,
        IRuntimeSystemProcessClassifier? system = null)
        => RuntimeProcessAttributionCatalogProviderTests.CreateProvider(new Registry(), packageIdentityResolver: package,
            serviceIdentityResolver: service, rootIdentityResolver: root, systemClassifier: system);
    private static async Task<RuntimeSoftwareAttribution> Capture(RuntimeProcessAttributionCatalogProvider provider,
        Cache cache, RuntimeProcessIdentity process)
    {
        cache.Prepare(process);
        return (await RuntimeProcessAttributionCapture.CaptureAsync(provider, [process], Scores, default,
            cache.Get, cache.Set)).Batch.SoftwareByProcessId[process.ProcessId];
    }
    private static async Task<RuntimeSoftwareAttribution> Uncached(RuntimeProcessAttributionCatalogProvider provider,
        RuntimeProcessIdentity process)
        => (await RuntimeProcessAttributionCapture.CaptureAsync(provider, [process], Scores, default))
            .Batch.SoftwareByProcessId[process.ProcessId];

    private sealed class Cache
    {
        private readonly ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution> entries = new();
        internal void Prepare(RuntimeProcessIdentity process) => WindowsResourceBreakdownSampler.CacheRuntimeIdentity(entries, process);
        internal WindowsResourceBreakdownSampler.CachedProcessAttribution Entry(RuntimeProcessIdentity process)
            => entries.TryGet(new(process.ProcessId, process.StartKey!.Value), out var entry) ? entry! : throw new InvalidOperationException();
        internal RuntimeProcessAttributionDirectMatch? Get(RuntimeProcessIdentity process, string scope,
            RuntimeProcessAttributionCatalog catalog) => WindowsResourceBreakdownSampler.TryGetCachedDirectMatch(entries, process, scope, catalog);
        internal void Set(RuntimeProcessIdentity process, string scope, RuntimeProcessAttributionCatalog catalog,
            RuntimeProcessAttributionDirectMatch match) => WindowsResourceBreakdownSampler.CacheDirectMatch(entries, process, scope, catalog, match);
    }
    private sealed class MutableResolver : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        internal RuntimeAttributionObservation Current = RuntimeAttributionObservation.NoMatch;
        internal bool Throw;
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => Throw ? throw new IOException("observation failed") : Current;
    }
    private sealed class Registry : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SoftwareRecord>>([]);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken) => GetSoftwareAsync(cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance() => now += TimeSpan.FromSeconds(5);
    }
}
