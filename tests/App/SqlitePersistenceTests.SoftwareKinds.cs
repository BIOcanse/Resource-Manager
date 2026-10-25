using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

public sealed partial class SqlitePersistenceTests
{
    [Fact]
    public async Task PortableCatalogGameKeepsGameKindInSchedulingAndIsExcludedFromMemoryReports()
    {
        const string executable = @"F:\Genshin Impact Game\YuanShen.exe";
        const string root = @"F:\Genshin Impact Game";
        var database = CreateDatabase();
        using var registry = CreatePortableRegistry(database);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            registry.Observe(new PortableSoftwareObservation(
                "catalog:game-genshin-impact", "game-genshin-impact", "Portable Genshin registration",
                SoftwareKinds.Game, executable, root, IdentityConfirmed: true));
            var registration = Assert.Single(registry.GetSnapshot());
            Assert.Equal(SoftwareKinds.Game, registration.Kind);
            var software = new SoftwareRecord(registration.SoftwareId, registration.Name,
                registration.Kind, "portable", [],
                registration.RootPaths, new(false, "None", "missing_uninstall_entry"), null);
            using var identityCatalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
                "game-genshin-impact", "Genshin Impact", SoftwareKinds.Game, "test",
                ExecutableNames: ["YuanShen.exe"]));
            using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [software], [], [],
                new NoIdentity(), new NoIdentity(), new NoIdentity(), new NoIdentity(), identityCatalog,
                SoftwareIdentityCatalogTestData.Owner);
            // Unconfirmed roots force the bundled-catalog/registered-payload branch.
            Assert.Empty(registration.RootPaths);
            var genshin = catalog.Pipeline.Match(new RuntimeProcessIdentity(
                4242, null, "YuanShen", executable, false));
            // The registration keeps its id and paths; name and kind come from the current catalog entry.
            Assert.Equal("Genshin Impact", genshin.Name);
            Assert.Equal(SoftwareKinds.Game, genshin.Kind);

            var inventoryFact = new SchedulingProcessFact(4242, 123, "YuanShen", executable,
                "", "", "", "", 0, SchedulingProcessMetricMask.None, 0, 0, 1, []);
            var fact = WindowsResourceBreakdownSampler.ProjectSchedulingAttribution(
                inventoryFact, genshin, CompiledBaseScorePlan.Empty, processKey: null);
            Assert.Equal(4242, fact.ProcessId);
            Assert.Equal(registration.SoftwareId, fact.SoftwareId);
            Assert.Equal("Genshin Impact", fact.SoftwareName);
            Assert.Equal(SoftwareKinds.Game, fact.SoftwareKind);
            Assert.Equal(95D, fact.BaseScore);
            Assert.Equal(5U, NativeSoftwareKindMap.ToNative(fact.SoftwareKind));
            Assert.Equal(NativeSmartCoordinatorSoftwareKind.Game,
                HostManagerSmartCoordinator.ToNativeSoftwareKind(fact.SoftwareKind));

            var now = DateTimeOffset.UtcNow;
            var snapshot = new ResourceBreakdownSnapshot(now,
                [new ResourceBreakdownBar(ResourceBreakdownMetricIds.MemoryUsage, "B",
                    ResourceBreakdownScaleModes.Capacity, 4_000, 10_000, 40,
                    [new ResourceSoftwareSegment(fact.SoftwareId, fact.SoftwareName, fact.SoftwareKind,
                        fact.SoftwareDisplayKind, 4_000, 40, 1,
                        [new ResourceProcessSegment(fact.ProcessId, fact.ProcessName, fact.ExecutablePath,
                            4_000, 40, 100)])])])
            {
                Datasets = [new ResourceBreakdownDatasetSamplingState(ResourceManager.App.Domain.Monitoring.SamplingDatasetIds.ProcessMemoryUsage, ResourceBreakdownSamplingStatuses.Ready, 1, 1, now, now, now, null, null, null)],
                Sampling = new ResourceBreakdownSamplingState(ResourceBreakdownSamplingStatuses.Ready,
                    1, now, now, null, null)
            };
            var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
            var rules = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator.Recreate.Rules
                .Where(rule => HostManagerSoftwareMemoryReportFactProjector.Supports(rule.FactKind)).ToArray();
            Assert.NotEmpty(rules);
            var report = HostManagerSoftwareMemoryReportFactProjector.Project(1, 2, rules, snapshot, targets, new HashSet<string> { fact.SoftwareId });
            Assert.Equal(NativeReportSourceStatus.Complete, report.Status);
            Assert.Empty(report.Facts);
            Assert.Empty(targets);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    private sealed class NoIdentity : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
