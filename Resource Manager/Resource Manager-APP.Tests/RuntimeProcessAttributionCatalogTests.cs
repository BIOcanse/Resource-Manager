using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class RuntimeProcessAttributionCatalogTests
{
    private readonly NullPackageIdentityResolver packageResolver = new();
    private readonly NullServiceIdentityResolver serviceResolver = new();
    private readonly NullRootIdentityResolver rootResolver = new();
    private readonly NullSystemProcessClassifier systemClassifier = new();

    [Fact]
    public void CreateOrReuse_ReusesCatalogAndPipelineForEquivalentSourceVersions()
    {
        var first = CreateCatalog(
            null,
            [CreateSoftware("software:one", "Software One", [@"D:\Apps\One"])],
            [],
            []);
        var reused = CreateCatalog(
            first,
            [CreateSoftware("software:one", "Software One", [@"D:\Apps\One"])],
            [],
            []);

        Assert.Same(first, reused);
        Assert.Same(first.Pipeline, reused.Pipeline);
        Assert.Equal(1, reused.Generation);
    }

    [Fact]
    public void CreateOrReuse_AdvancesGenerationForEveryAttributionSourceVersion()
    {
        var empty = CreateCatalog(null, [], [], []);
        var softwareChanged = CreateCatalog(
            empty,
            [CreateSoftware("software:one", "Software One", [@"D:\Apps\One"])],
            [],
            []);
        var adaptedChanged = CreateCatalog(
            softwareChanged,
            [CreateSoftware("software:one", "Software One", [@"D:\Apps\One"])],
            [CreateAdapter("adapter:one", "Adapter One", [@"D:\Apps\Adapter"])],
            []);
        var controlledChanged = CreateCatalog(
            adaptedChanged,
            [CreateSoftware("software:one", "Software One", [@"D:\Apps\One"])],
            [CreateAdapter("adapter:one", "Adapter One", [@"D:\Apps\Adapter"])],
            [CreateControlled(Guid.Parse("10000000-0000-0000-0000-000000000001"), [@"D:\Apps\Controlled"])]);

        Assert.Equal(1, empty.Generation);
        Assert.Equal(2, softwareChanged.Generation);
        Assert.Equal(3, adaptedChanged.Generation);
        Assert.Equal(4, controlledChanged.Generation);
        Assert.NotEqual(empty.Version.Software, softwareChanged.Version.Software);
        Assert.NotEqual(softwareChanged.Version.Adapted, adaptedChanged.Version.Adapted);
        Assert.NotEqual(adaptedChanged.Version.Controlled, controlledChanged.Version.Controlled);
    }

    [Fact]
    public void CreateOrReuse_DeepSnapshotsAttributionSources()
    {
        var softwareRoots = new List<string> { @"D:\Apps\Known" };
        var adapterRoots = new List<string> { @"D:\Apps\Adapter" };
        var adapterProcesses = new List<AdapterProcessDeclaration>();
        var controlledRoots = new List<string> { @"D:\Apps\Controlled" };
        var controlledProcesses = new List<ControlledProcessDeclaration>();
        var catalog = CreateCatalog(
            null,
            [CreateSoftware("software:known", "Known Software", softwareRoots)],
            [CreateAdapter("adapter:known", "Known Adapter", adapterRoots, adapterProcesses)],
            [CreateControlled(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                controlledRoots,
                controlledProcesses)]);

        softwareRoots.Add(@"D:\Apps\MutatedSoftware");
        adapterRoots.Add(@"D:\Apps\MutatedAdapter");
        adapterProcesses.Add(new AdapterProcessDeclaration("MutatedAdapter", null, null));
        controlledRoots.Add(@"D:\Apps\MutatedControlled");
        controlledProcesses.Add(new ControlledProcessDeclaration("MutatedControlled", null, null));

        Assert.Equal(
            RuntimeAttributionIds.Unattributed,
            catalog.Pipeline.Match(
                CreateProcess(10, "Other", @"D:\Apps\MutatedSoftware\app.exe")).Id);
        Assert.Equal(
            RuntimeAttributionIds.Unattributed,
            catalog.Pipeline.Match(
                CreateProcess(11, "MutatedAdapter", @"D:\Elsewhere\adapter.exe")).Id);
        Assert.Equal(
            RuntimeAttributionIds.Unattributed,
            catalog.Pipeline.Match(
                CreateProcess(12, "MutatedControlled", @"D:\Elsewhere\controlled.exe")).Id);

        var original = catalog.Pipeline.Match(CreateProcess(13, "Known", @"D:\Apps\Known\known.exe"));
        Assert.NotNull(original);
        var roots = Assert.IsAssignableFrom<IList<string>>(original.RootPaths);
        Assert.Throws<NotSupportedException>(() => roots.Add(@"D:\Apps\Injected"));
    }

    [Fact]
    public void CreateOrReuse_SharedRootsRemainAmbiguousWithoutInvalidatingNativeCatalog()
    {
        var catalog = CreateCatalog(
            null,
            [
                CreateSoftware("software:one", "Software One", [@"D:\Apps\Shared"]),
                CreateSoftware("software:two", "Software Two", [@"D:\Apps\Shared"])
            ],
            [],
            []);

        var attribution = catalog.Pipeline.Match(
            CreateProcess(20, "SharedChild", @"D:\Apps\Shared\child.exe"));

        Assert.Equal(RuntimeAttributionIds.Unattributed, attribution.Id);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("python3")]
    public void ControlledGenericExecutableKeepsPathAttributionWithoutNameAttribution(string name)
    {
        var id = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var catalog = CreateCatalog(null, [], [],
            [CreateControlled(id, [], [new ControlledProcessDeclaration(name, null, $@"D:\Apps\OwnedRuntime\{name}.exe")])]);

        Assert.Equal($"controlled-registration:{id}", catalog.Pipeline.Match(
            CreateProcess(21, name, $@"D:\Apps\OwnedRuntime\{name}.exe")).Id);
        Assert.Equal(RuntimeAttributionIds.Unattributed, catalog.Pipeline.Match(
            CreateProcess(22, name, $@"D:\Elsewhere\{name}.exe")).Id);
    }

    private RuntimeProcessAttributionCatalog CreateCatalog(
        RuntimeProcessAttributionCatalog? current,
        IReadOnlyList<SoftwareRecord> software,
        IReadOnlyList<AdapterSoftwareRegistration> adapted,
        IReadOnlyList<ControlledSoftwareRegistration> controlled)
    {
        return RuntimeProcessAttributionCatalog.CreateOrReuse(
            current,
            software,
            adapted,
            controlled,
            packageResolver,
            serviceResolver,
            rootResolver,
            systemClassifier,
            SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner);
    }

    private static SoftwareRecord CreateSoftware(
        string id,
        string name,
        IReadOnlyList<string> rootPaths)
    {
        return new SoftwareRecord(
            id,
            name,
            SoftwareKinds.Other,
            "其他软件",
            "installed",
            ["test"],
            rootPaths,
            "",
            new SoftwareOperationCapabilities(false, "None", "不可卸载", ""),
            null);
    }

    private static AdapterSoftwareRegistration CreateAdapter(
        string id,
        string displayName,
        IReadOnlyList<string> rootPaths,
        IReadOnlyList<AdapterProcessDeclaration>? processes = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdapterSoftwareRegistration(
            id,
            AdapterRegistrationSchemaVersions.Current,
            $"{id}.adapter",
            $"{id}.app",
            displayName,
            null,
            rootPaths,
            processes ?? [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                "http://127.0.0.1:9000/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, now, 200, "ok"),
            "registered",
            now,
            now);
    }

    private static ControlledSoftwareRegistration CreateControlled(
        Guid id,
        IReadOnlyList<string> rootPaths,
        IReadOnlyList<ControlledProcessDeclaration>? processes = null)
    {
        return new ControlledSoftwareRegistration(
            id,
            DateTimeOffset.UtcNow,
            "test.exe",
            ["Controlled Software"],
            rootPaths,
            processes ?? [],
            [],
            "controlled");
    }

    private static RuntimeProcessIdentity CreateProcess(int processId, string name, string executablePath)
    {
        return new RuntimeProcessIdentity(processId, null, name, executablePath, false);
    }

    private sealed class NullPackageIdentityResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NullServiceIdentityResolver : IRuntimeServiceIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NullRootIdentityResolver : IRuntimeRootIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NullSystemProcessClassifier : IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
