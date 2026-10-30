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
    public void UnavailableHelperCommandLineIsReadOncePerProcessInstance()
    {
        var cache = new ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution>();
        var reads = 0;
        string? Read() { reads++; return reads == 1 ? null : "child --type=utility"; }
        for (var i = 0; i < 20; i++)
            Assert.Null(WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, Child, Read).CommandLine);
        Assert.Equal(1, reads);
        var next = Child with { StartKey = Child.StartKey + 1 };
        Assert.Equal("child --type=utility", WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, next, Read).CommandLine);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void RepeatedIdentityReadsShareExactStringsWhenAnotherFieldChanges()
    {
        var cache = new ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution>();
        var original = WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, Child,
            () => new string("child --type=utility".ToCharArray()));
        for (var round = 0; round < 20; round++)
        {
            var incoming = Child with
            {
                Name = new string(Child.Name.ToCharArray()),
                ExecutablePath = new string(Child.ExecutablePath!.ToCharArray()),
                UserSid = new string(Sid.ToCharArray()),
                WindowTitle = $"changing title {round}"
            };
            var current = WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, incoming,
                () => throw new InvalidOperationException("Command line was already observed."));
            Assert.Same(original.Name, current.Name);
            Assert.Same(original.ExecutablePath, current.ExecutablePath);
            Assert.Same(original.UserSid, current.UserSid);
            Assert.Same(original.CommandLine, current.CommandLine);
            Assert.Equal(incoming.WindowTitle, current.WindowTitle);
            Assert.Same(current, WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, incoming,
                () => throw new InvalidOperationException("Command line was already observed.")));
        }
        var changedSid = Child with { UserSid = Sid + "2" };
        var changed = WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache, changedSid, () => null);
        Assert.Equal(changedSid.UserSid, changed.UserSid);
        Assert.NotSame(original.UserSid, changed.UserSid);
    }

    [Fact]
    public async Task ServiceMembershipAndDisplayNameFollowTheServiceOwnersSnapshot()
    {
        var clock = new Clock();
        IReadOnlyList<WindowsServiceRecord>? entries =
            [Service("SvcA", "Service A"), Service("SvcB", "Service B") with { ProcessId = 0, State = 1 }];
        var reads = 0;
        var services = new WindowsServiceIdentityResolver(() => { reads++; return entries; }, _ => 100, clock);
        using var provider = Provider(service: services, system: new WindowsSystemProcessClassifier());
        using var cache = new Cache();
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
        using var cache = new Cache();
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
        using var cache = new Cache();
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
        using var cache = new Cache();
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
    public async Task PublishedExternalInputChangesInvalidateEvenWhenIdentityAndCatalogAreUnchanged(string source)
    {
        var mutable = new MutableResolver();
        using var provider = Provider(package: source == "package" ? mutable : null,
            service: source == "service" ? mutable : null,
            root: source.StartsWith("root", StringComparison.Ordinal) ? mutable : null,
            system: source == "system" ? mutable : null);
        using var cache = new Cache();
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
        using var cache = new Cache();
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
        using var cache = new Cache();
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
            using var cache = new Cache();
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

    [Fact]
    public async Task IdenticalServiceAcquisitionDoesNotPublishAnInputChange()
    {
        var clock = new Clock();
        var reads = 0;
        var services = new WindowsServiceIdentityResolver(() => { reads++; return [Service("SvcA", "A")]; }, _ => 100, clock);
        using var provider = Provider(service: services);
        using var cache = new Cache();
        var first = await Capture(provider, cache, Child);
        var inputs = cache.Entry(Child).DirectMatch!.Value.ExternalInputs;
        clock.Advance();
        Assert.Equal(first, await Capture(provider, cache, Child));
        Assert.Equal(2, reads); // source acquisition continues at its existing rate
        Assert.Same(inputs, cache.Entry(Child).DirectMatch!.Value.ExternalInputs);
    }
    [Fact]
    public void ReadingAnInputVersionDoesNotAcquireTheServiceSource()
    {
        var clock = new Clock();
        var reads = 0;
        var services = new WindowsServiceIdentityResolver(() => { reads++; return [Service("SvcA", "A")]; }, _ => 100, clock);
        _ = services.InputVersion;
        Assert.Equal(0, reads);
        var acquired = services.CaptureInputVersion();
        Assert.Equal(1, reads);
        clock.Advance();
        Assert.Same(acquired, services.InputVersion);
        Assert.Equal(1, reads);
        Assert.Same(acquired, services.CaptureInputVersion()); // unchanged source reuses the published version
        Assert.Equal(2, reads);
    }
    [Fact]
    public async Task ServiceResolutionUsesTheBatchSnapshotWhenTimeAdvancesDuringAnotherObservation()
    {
        var clock = new Clock();
        IReadOnlyList<WindowsServiceRecord> entries = [Service("SvcA", "A")];
        var services = new WindowsServiceIdentityResolver(() => entries, _ => 100, clock);
        var package = new CallbackPackageResolver(() => { entries = [Service("SvcB", "B")]; clock.Advance(); });
        using var provider = Provider(package: package, service: services);
        using var cache = new Cache();
        Assert.Equal("windows-service:svca", (await Capture(provider, cache, Child)).Id);
        Assert.Equal("windows-service:svca", cache.Entry(Child).DirectMatch!.Value.ExternalInputs!.Service.Attribution!.Id);
        Assert.Equal("windows-service:svcb", (await Capture(provider, cache, Child)).Id);
    }
    private sealed class CallbackPackageResolver(Action capture) : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
        { capture(); return RuntimeAttributionObservation.NoMatch; }
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
            cache.Get, cache.Set, cache.GetFixedInputs, cache.Catalogs)).Batch.SoftwareByProcessId[process.ProcessId];
    }
    private static async Task<RuntimeSoftwareAttribution> Uncached(RuntimeProcessAttributionCatalogProvider provider,
        RuntimeProcessIdentity process)
        => (await RuntimeProcessAttributionCapture.CaptureAsync(provider, [process], Scores, default))
            .Batch.SoftwareByProcessId[process.ProcessId];

    // Holds the catalog readers the way the sampling run does, for as long as the test uses the cache.
    private sealed class Cache : IDisposable
    {
        private readonly ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution> entries = new();
        internal Dictionary<string, RuntimeProcessAttributionCatalogLease> Catalogs { get; } = new(StringComparer.Ordinal);
        public void Dispose() { foreach (var reader in Catalogs.Values) reader.Dispose(); Catalogs.Clear(); }
        internal void Prepare(RuntimeProcessIdentity process) => WindowsResourceBreakdownSampler.CacheRuntimeIdentity(entries, process);
        internal WindowsResourceBreakdownSampler.CachedProcessAttribution Entry(RuntimeProcessIdentity process)
            => entries.TryGet(new(process.ProcessId, process.StartKey!.Value), out var entry) ? entry! : throw new InvalidOperationException();
        internal RuntimeProcessAttributionDirectMatch? Get(RuntimeProcessIdentity process, string scope,
            RuntimeProcessAttributionCatalog catalog) => WindowsResourceBreakdownSampler.TryGetCachedDirectMatch(entries, process, scope, catalog);
        internal RuntimeProcessAttributionFixedInputs? GetFixedInputs(RuntimeProcessIdentity process) => Entry(process).FixedInputs;
        internal void Set(RuntimeProcessIdentity process, string scope, RuntimeProcessAttributionCatalog catalog,
            RuntimeProcessAttributionDirectMatch match) => WindowsResourceBreakdownSampler.CacheDirectMatch(entries, process, scope, catalog, match);
    }
    private sealed class MutableResolver : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        private RuntimeAttributionObservation current = RuntimeAttributionObservation.NoMatch;
        private bool fail;
        private object version = new();
        public object InputVersion => version;
        // Represents the source's input-change event: publish the new value,
        // then replace the version consumed by the next batch.
        internal RuntimeAttributionObservation Current
        {
            get => current;
            set { current = value; version = new(); }
        }
        internal bool Throw
        {
            get => fail;
            set { fail = value; version = new(); }
        }
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
