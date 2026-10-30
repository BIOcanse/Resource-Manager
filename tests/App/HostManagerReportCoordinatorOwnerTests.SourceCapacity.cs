using Microsoft.Extensions.Logging;
using System.Reflection;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Fact]
    public async Task SourceCapacityGrowsOnceToNativeDemandAndSurvivesWorkspaceReload()
    {
        var host = SourceCapacityPlan(128);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = host });
        var resources = new CachedResourceSource();
        var processes = new PopulationProcessSource();
        var log = new SourceLog();
        NativeReportCoordinatorWorkspace? workspace = null;
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(), resources, new UnavailableInterruptSource(), new EmptyProtectionService(), log,
            OperationScratchFixture.Create, processes, new RegisteredSoftware(false),
            (plan, low, high, scratch) => workspace = new NativeReportCoordinatorWorkspace(plan, low, high, scratch));
        await using var hold = SourceTestHold(owner);
        PublishPopulation(30, 1, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(64U, workspace!.Session.Capacity.ObservationCapacity);
        PublishPopulation(60, 2, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(120U, workspace.Session.Capacity.ObservationCapacity);
        var bytes = workspace.Session.WorkspaceCapacity.ResidentByteCount;
        PublishPopulation(60, 3, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(120U, workspace.Session.Capacity.ObservationCapacity);
        Assert.Equal(bytes, workspace.Session.WorkspaceCapacity.ResidentByteCount);
        Assert.Empty(log.Entries);
        // A replacement imports more observations than the original limit.
        provider.Publish(provider.Current with { Version = 2, HostManager = SourceCapacityPlan(128, 2) });
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(120U, workspace.Session.Capacity.ObservationCapacity);
        Assert.Equal("completed", owner.GetStatus().EvaluationState);
    }

    [Fact]
    public async Task SourceBoundFailureIsTypedLoggedOnceAndRecoversWhenPopulationDrops()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = SourceCapacityPlan(128) });
        var resources = new CachedResourceSource();
        var processes = new PopulationProcessSource();
        var log = new SourceLog();
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(), resources, new UnavailableInterruptSource(), new EmptyProtectionService(), log,
            OperationScratchFixture.Create, processes, new RegisteredSoftware(false));
        await using var hold = SourceTestHold(owner);
        PublishPopulation(30, 1, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        for (var count = 65; count <= 67; count++)
        {
            PublishPopulation(count, count, resources, processes);
            var failure = await Assert.ThrowsAsync<ReportSourceObservationException>(() => owner.RefreshReportsAsync(CancellationToken.None));
            Assert.Equal(ReportSourceFailureReason.ObservationCapacityExceeded, failure.Result.Reason);
            Assert.Equal(NativeReportCoordinatorStatus.BufferTooSmall, failure.Result.Status);
            Assert.Equal((uint)(count * 2), failure.Result.RequiredObservationCount);
            Assert.Equal("failed", owner.GetStatus().EvaluationState);
            Assert.Contains("background_memory_share", owner.GetStatus().UnavailableRules);
            Assert.Contains("background_memory_bytes", owner.GetStatus().UnavailableRules);
        }
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(134U, ReportOwnerField<Dictionary<(ulong Source, ulong Coverage), NativeReportObservationResult>>(
            owner, "sourceFailures")![(14, 14)].RequiredObservationCount);
        PublishPopulation(30, 68, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal("completed", owner.GetStatus().EvaluationState);
        Assert.DoesNotContain("background_memory_share", owner.GetStatus().UnavailableRules);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        PublishPopulation(30, 69, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task FailedNewPopulationStillSettlesExpiredRowsAndThenRecovers()
    {
        var host = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var capacity = root["host_recreate"]!["report_coordinator"]!["capacity"]!;
            capacity["maximum_observation_count"] = 64;
            capacity["maximum_rolling_observation_count"] = 64;
            capacity["observation_index_capacity"] = 128;
            foreach (var rule in root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray())
                if (rule!["source_handle"]!.GetValue<int>() == 14)
                {
                    rule["stale_after_ms"] = 1;
                    rule["retention_ms"] = 2000;
                }
        }, editFreedomPoints: root => FreedomPointTestFactory.Point(root,
            BackendFreedomPointPaths.ReportObservationGrowthLimit)["value"] = 128);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = host });
        var resources = new CachedResourceSource();
        var processes = new PopulationProcessSource();
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var log = new SourceLog();
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), persistence, new CachedMetricSource(), resources,
            new UnavailableInterruptSource(), new EmptyProtectionService(), log, OperationScratchFixture.Create,
            processes, new RegisteredSoftware(false));
        await using var hold = SourceTestHold(owner);
        await owner.RefreshReportsAsync(CancellationToken.None);
        PublishPopulation(64, 1, resources, processes);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(128, (await persistence.LoadAsync(CancellationToken.None))
            .Count(row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation && row.SourceHandle == 14));
        await Task.Delay(2100);
        // A smaller, entirely new population; retaining the old identities must
        // not deadlock the maintenance that eventually frees them.
        PublishPopulation(1, 2, resources, processes, "new");
        await Assert.ThrowsAsync<ReportSourceObservationException>(() => owner.RefreshReportsAsync(CancellationToken.None));
        Assert.DoesNotContain(await persistence.LoadAsync(CancellationToken.None),
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation && row.SourceHandle == 14);
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal("completed", owner.GetStatus().EvaluationState);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    private static CompiledHostManagerPlan SourceCapacityPlan(int limit, ulong epoch = 1)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var capacity = root["host_recreate"]!["report_coordinator"]!["capacity"]!;
            capacity["maximum_observation_count"] = 64;
            capacity["maximum_rolling_observation_count"] = 64;
            capacity["observation_index_capacity"] = 128;
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + checked((int)epoch);
        }, planEpoch: epoch, editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.ReportObservationGrowthLimit)["value"] = limit);

    private static IAsyncDisposable SourceTestHold(HostManagerReportCoordinatorOwner owner)
        => (IAsyncDisposable)typeof(HostManagerReportCoordinatorOwner)
            .GetMethod("AcquireWorkspaceHold", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [CancellationToken.None])!;

    private static void PublishPopulation(int count, long generation, CachedResourceSource resources, PopulationProcessSource processes, string prefix = "entry")
    {
        var snapshot = CreateSoftwareMemorySnapshot(generation, DateTimeOffset.UtcNow, 1);
        var bar = snapshot.Bars.Single();
        resources.Publish(snapshot with { Bars = [bar with { Software = Enumerable.Range(0, count)
            .Select(index => bar.Software.Single() with { SoftwareId = $"software:{prefix}{index}" }).ToArray() }] });
        processes.Count = count;
        processes.Prefix = prefix;
    }

    private sealed class PopulationProcessSource : ISchedulingProcessFactObservationSource
    {
        internal int Count;
        internal string Prefix = "entry";
        private readonly BackgroundProcessSource inner = new();
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
            => inner.AcquireSubscription(id, mask, interval);
        public SchedulingProcessFactSnapshot ReadLatest(SchedulingProcessFactRequest request)
        {
            var snapshot = inner.ReadLatest(request);
            return snapshot with
            {
                EnumeratedCount = checked((uint)Count),
                EmittedCount = checked((uint)Count),
                Processes = Enumerable.Range(0, Count).Select(index => snapshot.Processes.Single()
                    with { SoftwareId = $"software:{Prefix}{index}", ProcessId = index + 100 }).ToArray()
            };
        }
    }

    private sealed class SourceLog : ILogger<HostManagerReportCoordinatorOwner>
    {
        internal List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }
}
