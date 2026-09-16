using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerReportCoordinatorOwnerTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"rm-report-owner-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitialSettlementPublishesThePersistedPlanWithoutReplanning()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var database = CreateDatabase();
        var persistence = new NativeReportCoordinatorPersistenceStore(database);
        var metrics = new CachedMetricSource();
        var resources = new CachedResourceSource();
        var interrupts = new UnavailableInterruptSource();
        using var owner = new HostManagerReportCoordinatorOwner(
            provider,
            new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(),
            persistence,
            metrics,
            resources,
            interrupts,
            new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await owner.StartAsync(startupTimeout.Token);
        try
        {
            var rows = await persistence.LoadAsync(CancellationToken.None);
            var metadata = Assert.Single(rows);

            Assert.Equal(
                (uint)NativeReportPersistenceKind.Metadata,
                metadata.OperationKind);
            Assert.True(metadata.MutationVersion > 0);
            Assert.Equal(8, owner.GetStatus().ConfiguredRuleCount);
            Assert.Equal(1, metrics.ActiveSubscriptions);
            Assert.Equal(1, resources.ActiveSubscriptions);
            Assert.Equal(1, interrupts.ActiveSubscriptions);
        }
        finally
        {
            await owner.StopAsync(CancellationToken.None);
        }
        Assert.Equal(0, metrics.ActiveSubscriptions);
        Assert.Equal(0, resources.ActiveSubscriptions);
        Assert.Equal(0, interrupts.ActiveSubscriptions);
    }

    [Fact]
    public async Task ActiveOwnerAttemptHoldsPublicationUntilItsCommitCompletes()
    {
        var firstHostPlan = HostManagerTestPlanFactory.CreatePlan();
        var secondHostPlan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var firstRuntimePlan = CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = firstHostPlan
        };
        var secondRuntimePlan = firstRuntimePlan with
        {
            Version = 2,
            HostManager = secondHostPlan
        };
        provider.Publish(firstRuntimePlan);
        var database = CreateDatabase();
        var persistence = new BlockingPersistenceStore(
            new NativeReportCoordinatorPersistenceStore(database));

        using var owner = new HostManagerReportCoordinatorOwner(
            provider,
            new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(),
            persistence,
            new CachedMetricSource(),
            new CachedResourceSource(),
            new UnavailableInterruptSource(),
            new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startup = owner.StartAsync(timeout.Token);

        await persistence.PersistEntered.WaitAsync(timeout.Token);
        Assert.NotNull(deployment.Snapshot.ReportCoordinator.ActiveAttempt);
        using var publishStarted = new ManualResetEventSlim(false);
        var publish = Task.Run(() =>
        {
            publishStarted.Set();
            return provider.Publish(secondRuntimePlan);
        }, timeout.Token);
        publishStarted.Wait(timeout.Token);
        await Task.Delay(50, timeout.Token);

        var publishWasBlocked = !publish.IsCompleted;
        var currentWhileBlocked = provider.Current;
        persistence.Release();
        await startup.WaitAsync(timeout.Token);
        await publish.WaitAsync(timeout.Token);

        Assert.True(publishWasBlocked);
        Assert.Same(firstRuntimePlan, currentWhileBlocked);
        Assert.Same(secondRuntimePlan, provider.Current);
        var reportDeployment = deployment.Snapshot.ReportCoordinator;
        Assert.Null(reportDeployment.ActiveAttempt);
        Assert.Equal(firstHostPlan.PlanEpoch, reportDeployment.AppliedPlanEpoch);
        Assert.Equal(secondHostPlan.PlanEpoch, reportDeployment.DesiredPlanEpoch);
        await owner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StagingFailureSettlesTheAttemptExactlyOnce()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        using var owner = new HostManagerReportCoordinatorOwner(
            provider,
            new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(),
            new FailingPersistenceStore(
                new NativeReportCoordinatorPersistenceStore(CreateDatabase())),
            new CachedMetricSource(),
            new CachedResourceSource(),
            new UnavailableInterruptSource(),
            new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.StartAsync(timeout.Token));

        Assert.Equal("injected-persist-failure", failure.Message);
        var reportDeployment = deployment.Snapshot.ReportCoordinator;
        Assert.Null(reportDeployment.ActiveAttempt);
        Assert.Equal(1UL, reportDeployment.LastSettledAttemptId);
        Assert.Equal(0U, reportDeployment.StaleCompletionCount);
        var recorded = Assert.IsType<HostManagerModuleFailureSnapshot>(
            reportDeployment.LastFailure);
        Assert.True(recorded.Active);
        Assert.Equal("report-coordinator-apply-failed", recorded.StableCode);
    }

    [Fact]
    public void CommittedSourceIdentityIsNotObservedTwice()
    {
        var state = new CoordinatorState([]);
        var observedAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var source = new HostManagerReportSourceObservation(
            9,
            9,
            4,
            observedAt,
            NativeReportSourceStatus.Complete,
            1000,
            []);

        Assert.True(state.ShouldObserveSource(source));
        var frame = state.PreviewNextSourceFrame(source);
        state.CommitSourceObservation(source, frame, []);
        Assert.False(state.ShouldObserveSource(source));
        Assert.True(state.ShouldObserveSource(source with
        {
            ObservedAt = observedAt.AddSeconds(10)
        }));
    }

    [Fact]
    public void SourceAndFactHighWaterAdvanceOnlyAfterCommit()
    {
        var state = new CoordinatorState([]);
        var source = new HostManagerReportSourceObservation(
            9,
            9,
            4,
            DateTimeOffset.UtcNow,
            NativeReportSourceStatus.Complete,
            1000,
            []);

        var firstFrame = state.PreviewNextSourceFrame(source);
        var repeatedFrame = state.PreviewNextSourceFrame(source);
        var firstSequence = state.PreviewNextFactSequence(11, 12);
        var repeatedSequence = state.PreviewNextFactSequence(11, 12);

        Assert.Equal(firstFrame, repeatedFrame);
        Assert.Equal(firstSequence, repeatedSequence);

        state.CommitSourceObservation(
            source,
            firstFrame,
            [
                new NativeReportFactInput
                {
                    RuleHandle = 11,
                    TargetHandle = 12,
                    FactSequence = firstSequence
                }
            ]);

        Assert.Equal(
            firstFrame.SourceGeneration,
            state.PreviewNextSourceFrame(source).SourceGeneration);
        Assert.Equal(
            firstFrame.SnapshotEpoch + 1,
            state.PreviewNextSourceFrame(source).SnapshotEpoch);
        Assert.Equal(
            firstSequence + 1,
            state.PreviewNextFactSequence(11, 12));
    }

    [Fact]
    public async Task UniqueSoftwareMemorySnapshotsCreateAndClearOneReportAndSignal()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var resources = new CachedResourceSource();
        using var owner = new HostManagerReportCoordinatorOwner(
            provider,
            new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(),
            new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(),
            resources,
            new UnavailableInterruptSource(),
            new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await owner.StartAsync(timeout.Token);
        try
        {
            resources.Publish(CreateSoftwareMemorySnapshot(
                generation: 41,
                firstObservedAt,
                systemPercent: 18));
            var first = await owner.RefreshReportsAsync(timeout.Token);
            var duplicate = await owner.RefreshReportsAsync(timeout.Token);

            Assert.Empty(first.Reports);
            Assert.Empty(duplicate.Reports);
            Assert.Equal(first.Status.LastObservedAt, duplicate.Status.LastObservedAt);

            resources.Publish(CreateSoftwareMemorySnapshot(
                generation: 42,
                firstObservedAt.AddSeconds(10),
                systemPercent: 18));
            var second = await owner.RefreshReportsAsync(timeout.Token);
            Assert.Empty(second.Reports);

            resources.Publish(CreateSoftwareMemorySnapshot(
                generation: 43,
                firstObservedAt.AddSeconds(20),
                systemPercent: 18));
            var activated = await owner.RefreshReportsAsync(timeout.Token);

            // 内存有两条规则（百分比与绝对字节），但它们共用一个 family，
            // 所以同一个软件只出一条报告 —— 任一越线即可，不是各报各的。
            var report = Assert.Single(activated.Reports);
            Assert.Equal(2, activated.Status.AvailableRuleCount);
            Assert.Equal("software:editor", report.Target.SoftwareId);
            Assert.Equal(OptimizationReportTargetTypes.Software, report.Target.TargetType);
            Assert.Equal(OptimizationResourceKinds.Memory, report.Evidence.ResourceKind);
            var signal = Assert.Single(owner.ReadSoftwareIssueSnapshot().Signals);
            Assert.Equal(report.Id, signal.ReportId);
            Assert.Equal("software:editor", signal.SoftwareId);

            resources.Publish(CreateSoftwareMemorySnapshot(
                generation: 44,
                firstObservedAt.AddSeconds(30),
                systemPercent: 5));
            var firstMiss = await owner.RefreshReportsAsync(timeout.Token);
            Assert.Single(firstMiss.Reports);

            resources.Publish(CreateSoftwareMemorySnapshot(
                generation: 45,
                firstObservedAt.AddSeconds(40),
                systemPercent: 5));
            var cleared = await owner.RefreshReportsAsync(timeout.Token);

            Assert.Empty(cleared.Reports);
            Assert.Empty(owner.ReadSoftwareIssueSnapshot().Signals);
            Assert.NotNull(cleared.Status.LastEvaluationAt);
            Assert.Equal(
                firstObservedAt.AddSeconds(40),
                cleared.Status.LastObservedAt);
        }
        finally
        {
            await owner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ReplacementRestoresOnlyExactCommittedSourceIdentities()
    {
        var previous = new CoordinatorState([]);
        var source = new HostManagerReportSourceObservation(
            9, 10, 41, DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            NativeReportSourceStatus.Complete, 1000, []);
        var frame = previous.PreviewNextSourceFrame(source);
        previous.CommitSourceObservation(source, frame, []);
        var checkpoint = new NativeReportPersistenceOperation
        {
            OperationKind = (uint)NativeReportPersistenceKind.Source,
            IdentityHandle = 1,
            SourceHandle = source.SourceHandle,
            CoverageScopeHandle = source.CoverageScopeHandle,
            SourceGeneration = frame.SourceGeneration,
            SourceSnapshotEpoch = frame.SnapshotEpoch,
            SourceStatus = (uint)source.Status,
            LastObservedAtUtcMilliseconds = source.ObservedAt.AddSeconds(30).ToUnixTimeMilliseconds()
        };
        var exact = new CoordinatorState([checkpoint]);
        exact.RestoreCommittedSourceFrames(previous);
        Assert.False(exact.ShouldObserveSource(source));
        Assert.Equal(source.ObservedAt, exact.LastObservationAt);
        Assert.Equal(frame.SourceGeneration, exact.PreviewNextSourceFrame(source).SourceGeneration);
        Assert.Equal(frame.SnapshotEpoch + 1, exact.PreviewNextSourceFrame(source).SnapshotEpoch);
        Assert.True(exact.ShouldObserveSource(source with { ObservedAt = source.ObservedAt.AddSeconds(1) }));
        Assert.True(exact.ShouldObserveSource(source with { ProviderGeneration = 42 }));

        NativeReportPersistenceOperation[] mismatches =
        [
            checkpoint with { SourceHandle = 11 },
            checkpoint with { CoverageScopeHandle = 11 },
            checkpoint with { SourceGeneration = frame.SourceGeneration + 1 },
            checkpoint with { SourceSnapshotEpoch = frame.SnapshotEpoch + 1 },
            checkpoint with { SourceStatus = (uint)NativeReportSourceStatus.Unavailable }
        ];
        foreach (var mismatch in mismatches)
        {
            var replacement = new CoordinatorState([mismatch]);
            replacement.RestoreCommittedSourceFrames(previous);
            Assert.True(replacement.ShouldObserveSource(source));
            Assert.Null(replacement.LastObservationAt);
        }
        var missing = new CoordinatorState([]);
        missing.RestoreCommittedSourceFrames(previous);
        Assert.True(missing.ShouldObserveSource(source));
        Assert.False(previous.ShouldObserveSource(source));
    }

    [Fact]
    public void ReplacementDoesNotInheritUnpersistedSourceProgressOrMutateItsPredecessor()
    {
        var previous = new CoordinatorState([]);
        var source = new HostManagerReportSourceObservation(
            9, 10, 41, DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            NativeReportSourceStatus.Complete, 1000, []);
        var committed = previous.PreviewNextSourceFrame(source);
        previous.CommitSourceObservation(source, committed, []);
        var checkpoint = new NativeReportPersistenceOperation
        {
            OperationKind = (uint)NativeReportPersistenceKind.Source,
            IdentityHandle = 1,
            SourceHandle = source.SourceHandle,
            CoverageScopeHandle = source.CoverageScopeHandle,
            SourceGeneration = committed.SourceGeneration,
            SourceSnapshotEpoch = committed.SnapshotEpoch,
            SourceStatus = (uint)source.Status
        };
        var latest = source with { ObservedAt = source.ObservedAt.AddSeconds(10) };
        var unpersisted = previous.PreviewNextSourceFrame(latest);
        previous.CommitSourceObservation(latest, unpersisted, []);
        var replacement = new CoordinatorState([checkpoint]);
        replacement.RestoreCommittedSourceFrames(previous);

        Assert.True(replacement.ShouldObserveSource(latest));
        Assert.Null(replacement.LastObservationAt);
        Assert.Equal(committed.SourceGeneration + 1, replacement.PreviewNextSourceFrame(latest).SourceGeneration);
        Assert.False(previous.ShouldObserveSource(latest));
        Assert.Equal(unpersisted.SnapshotEpoch + 1, previous.PreviewNextSourceFrame(latest).SnapshotEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanReloadDoesNotConsumeCachedSampleAgain(bool changeRule)
    {
        var firstPlan = HostManagerTestPlanFactory.CreatePlan();
        var nextPlan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            if (changeRule)
            {
                var memoryRule = root["host_recreate"]!["report_coordinator"]!["rules"]!
                    .AsArray().Single(rule => rule!["rule_handle"]!.GetValue<ulong>() == 1037)!;
                memoryRule["activation_threshold"] = 16;
            }
        }, planEpoch: 2);
        var nextRule = Assert.Single(nextPlan.ReportCoordinator.Recreate.Rules, rule => rule.RuleHandle == 1037);
        var firstRule = Assert.Single(firstPlan.ReportCoordinator.Recreate.Rules, rule => rule.RuleHandle == 1037);
        Assert.Equal(changeRule, firstRule.RuleGeneration != nextRule.RuleGeneration);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = firstPlan });
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var resources = new CachedResourceSource();
        using var owner = new HostManagerReportCoordinatorOwner(
            provider, new HostManagerReportCoordinatorRuntime(deployment), new HostManagerRuntimeIdentity(),
            persistence, new CachedMetricSource(), resources, new UnavailableInterruptSource(),
            new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow.AddSeconds(-20);
        await owner.StartAsync(timeout.Token);
        try
        {
            for (var sample = 1; sample <= 2; sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41, start.AddSeconds(sample), 18));
                Assert.Empty((await owner.RefreshReportsAsync(timeout.Token)).Reports);
            }
            var sourceBefore = Assert.Single(await persistence.LoadAsync(timeout.Token), row =>
                row.OperationKind == (uint)NativeReportPersistenceKind.Source && row.SourceHandle == firstRule.SourceHandle);
            provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = nextPlan });
            for (var refresh = 0; refresh < 2; refresh++)
            {
                var current = await owner.RefreshReportsAsync(timeout.Token);
                Assert.Empty(current.Reports);
                Assert.Equal(start.AddSeconds(2).ToUnixTimeMilliseconds(),
                    current.Status.LastObservedAt?.ToUnixTimeMilliseconds());
            }
            var rows = await persistence.LoadAsync(timeout.Token);
            var sourceAfter = Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Source
                && row.SourceHandle == firstRule.SourceHandle);
            Assert.Equal(sourceBefore.SourceGeneration, sourceAfter.SourceGeneration);
            Assert.Equal(sourceBefore.SourceSnapshotEpoch, sourceAfter.SourceSnapshotEpoch);
            var observations = rows.Where(row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation
                && row.RuleHandle == nextRule.RuleHandle && row.RuleGeneration == nextRule.RuleGeneration).ToArray();
            if (changeRule)
            {
                Assert.Empty(observations);
            }
            else
            {
                Assert.Equal(2UL, Assert.Single(observations).SampleCount);
            }
            var freshSamples = changeRule ? 3 : 1;
            for (var index = 1; index <= freshSamples; index++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41, start.AddSeconds(2 + index), 18));
                var current = await owner.RefreshReportsAsync(timeout.Token);
                Assert.Equal(index == freshSamples ? 1 : 0, current.Reports.Count);
            }
            Assert.Equal("software:editor", Assert.Single(owner.ReadSoftwareIssueSnapshot().Signals).SoftwareId);
            Assert.Equal(2UL, deployment.Snapshot.ReportCoordinator.AppliedPlanEpoch);
        }
        finally
        {
            await owner.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AbsentSoftwareCheckpointSurvivesPlanReloadAndCanReportAgain(
        bool initiallyActive, bool restartProvider)
    {
        var firstPlan = HostManagerTestPlanFactory.CreatePlan();
        var nextPlan = HostManagerTestPlanFactory.CreatePlan(
            root => root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1,
            planEpoch: 2);
        var firstRule = Assert.Single(firstPlan.ReportCoordinator.Recreate.Rules,
            rule => rule.RuleHandle == 1037);
        var nextRule = Assert.Single(nextPlan.ReportCoordinator.Recreate.Rules,
            rule => rule.RuleHandle == 1037);
        Assert.Equal(firstRule.RuleGeneration, nextRule.RuleGeneration);
        Assert.Equal(2U, firstRule.RequiredConsecutiveMisses);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = firstPlan });
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var metrics = new CachedMetricSource();
        var resources = new CachedResourceSource();
        var interrupts = new UnavailableInterruptSource();
        using var owner = new HostManagerReportCoordinatorOwner(
            provider,
            new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(),
            persistence,
            metrics,
            resources,
            interrupts,
            new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow.AddSeconds(-20);
        var sample = 0;
        var initialSamples = initiallyActive ? 3 : 1;
        await owner.StartAsync(timeout.Token);
        try
        {
            for (var index = 0; index < initialSamples; index++)
            {
                sample++;
                resources.Publish(CreateSoftwareMemorySnapshot(restartProvider ? 41 + sample : 41, start.AddSeconds(sample),
                    initiallyActive ? 18 : 5));
                await owner.RefreshReportsAsync(timeout.Token);
            }
            Assert.Equal(initiallyActive ? 1 : 0, owner.ReadSoftwareIssueSnapshot().Signals.Count);
            for (var index = 0; index < 7; index++)
            {
                sample++;
                var empty = CreateSoftwareMemorySnapshot(restartProvider ? 41 + sample : 41, start.AddSeconds(sample), 0);
                resources.Publish(empty with { Bars = [empty.Bars[0] with { Software = [] }] });
                await owner.RefreshReportsAsync(timeout.Token);
            }
            // 内存有两条规则（百分比 1037、绝对字节 1038），各自有一行观测。
            // 这个用例盯的是百分比那条的缺席检查点。
            var before = Assert.Single(await persistence.LoadAsync(timeout.Token),
                row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation
                    && row.RuleHandle == 1037);
            Assert.Equal(7U, before.ConsecutiveMisses);
            Assert.Equal((ulong)initialSamples, before.SampleCount);
            Assert.Equal(0U, before.Flags & (uint)NativeReportPersistenceFlags.Active);
            Assert.Empty(owner.ReadSoftwareIssueSnapshot().Signals);

            provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = nextPlan });
            var reloaded = await owner.RefreshReportsAsync(timeout.Token);
            var after = Assert.Single(await persistence.LoadAsync(timeout.Token),
                row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation
                    && row.RuleHandle == 1037);
            Assert.Equal(2UL, deployment.Snapshot.ReportCoordinator.AppliedPlanEpoch);
            Assert.False(deployment.Snapshot.ReportCoordinator.LastFailure?.Active ?? false);
            Assert.Equal(before.IdentityHandle, after.IdentityHandle);
            Assert.Equal(before.ConsecutiveMisses, after.ConsecutiveMisses);
            Assert.Equal(before.SampleCount, after.SampleCount);
            Assert.Empty(reloaded.Reports);

            for (var index = 0; index < 3; index++)
            {
                sample++;
                resources.Publish(CreateSoftwareMemorySnapshot(restartProvider ? 41 + sample : 41, start.AddSeconds(sample), 18));
                var refreshed = await owner.RefreshReportsAsync(timeout.Token);
                Assert.Equal(index == 2 ? 1 : 0, refreshed.Reports.Count);
            }
            Assert.Equal("software:editor", Assert.Single(owner.ReadSoftwareIssueSnapshot().Signals).SoftwareId);
            Assert.Equal(1, metrics.ActiveSubscriptions);
            Assert.Equal(1, resources.ActiveSubscriptions);
            Assert.Equal(1, interrupts.ActiveSubscriptions);
        }
        finally
        {
            await owner.StopAsync(CancellationToken.None);
        }
        Assert.Equal(0, metrics.ActiveSubscriptions);
        Assert.Equal(0, resources.ActiveSubscriptions);
        Assert.Equal(0, interrupts.ActiveSubscriptions);
    }

    [Fact]
    public async Task ConcurrentRefreshDoesNotReplayAnOlderCacheReadAfterANewerSnapshot()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = hostPlan });
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var resources = new CachedResourceSource();
        using var pausedSource = new PausedResourceSource(resources);
        using var owner = new HostManagerReportCoordinatorOwner(
            provider, new HostManagerReportCoordinatorRuntime(deployment), new HostManagerRuntimeIdentity(),
            persistence, new CachedMetricSource(), pausedSource, new UnavailableInterruptSource(),
            new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        Task<OptimizationReportOverview>? older = null;
        Task<OptimizationReportOverview>? newer = null;
        await owner.StartAsync(timeout.Token);
        try
        {
            resources.Publish(CreateSoftwareMemorySnapshot(41, observedAt, 18));
            await owner.RefreshReportsAsync(timeout.Token);
            pausedSource.PauseNextRead();
            older = Task.Run(() => owner.RefreshReportsAsync(timeout.Token), timeout.Token);
            await pausedSource.ReadPaused.WaitAsync(timeout.Token);
            resources.Publish(CreateSoftwareMemorySnapshot(41, observedAt.AddSeconds(10), 18));
            newer = owner.RefreshReportsAsync(timeout.Token);
            var readOutOfOrder = pausedSource.ReadWhilePaused;
            if (readOutOfOrder)
            {
                await newer.WaitAsync(timeout.Token);
            }
            pausedSource.Release();
            await Task.WhenAll(older, newer).WaitAsync(timeout.Token);

            // 内存两条规则各有一行观测，这里只看百分比那条的采样计数。
            var observation = Assert.Single(await persistence.LoadAsync(timeout.Token),
                row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation
                    && row.RuleHandle == 1037);
            Assert.Equal(2UL, observation.SampleCount);
            Assert.Equal(2U, observation.ConsecutiveHits);
            Assert.Empty((await owner.GetReportsAsync(timeout.Token)).Reports);
            Assert.False(readOutOfOrder);
        }
        finally
        {
            pausedSource.Release();
            try
            {
                if (older is not null) await older.WaitAsync(timeout.Token);
                if (newer is not null) await newer.WaitAsync(timeout.Token);
            }
            finally
            {
                await owner.StopAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private ResourceManagerDatabase CreateDatabase()
    {
        var contentRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new ResourceManagerDatabase(new TestHostEnvironment(contentRoot));
    }

    private static ResourceBreakdownSnapshot CreateSoftwareMemorySnapshot(
        long generation,
        DateTimeOffset capturedAt,
        double systemPercent)
    {
        var value = systemPercent * 100;
        return new ResourceBreakdownSnapshot(
            capturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "Memory",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    value,
                    10_000,
                    systemPercent,
                    $"{systemPercent:0.##}%",
                    [
                        new ResourceSoftwareSegment(
                            "software:editor",
                            "Editor",
                            SoftwareKinds.Other,
                            SoftwareKinds.Other,
                            value,
                            systemPercent,
                            $"{systemPercent:0.##}%",
                            1,
                            [
                                new ResourceProcessSegment(
                                    101,
                                    "Editor.exe",
                                    @"C:\Tools\Editor.exe",
                                    value,
                                    systemPercent,
                                    100,
                                    $"{systemPercent:0.##}%")
                            ])
                    ])
            ])
        {
            Sampling = new ResourceBreakdownSamplingState(
                ResourceBreakdownSamplingStatuses.Ready,
                generation,
                capturedAt,
                capturedAt,
                null,
                null)
        };
    }

    private sealed class BlockingPersistenceStore(
        INativeReportCoordinatorPersistenceStore inner)
        : INativeReportCoordinatorPersistenceStore
    {
        private readonly TaskCompletionSource persistEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task PersistEntered => persistEntered.Task;

        public Task<NativeReportPersistenceOperation[]> LoadAsync(
            CancellationToken cancellationToken)
            => inner.LoadAsync(cancellationToken);

        public async Task PersistAsync(
            ReadOnlyMemory<NativeReportPersistenceOperation> operations,
            CancellationToken cancellationToken)
        {
            persistEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await inner.PersistAsync(operations, cancellationToken);
        }

        internal void Release() => release.TrySetResult();
    }

    private sealed class FailingPersistenceStore(
        INativeReportCoordinatorPersistenceStore inner)
        : INativeReportCoordinatorPersistenceStore
    {
        public Task<NativeReportPersistenceOperation[]> LoadAsync(
            CancellationToken cancellationToken)
            => inner.LoadAsync(cancellationToken);

        public Task PersistAsync(
            ReadOnlyMemory<NativeReportPersistenceOperation> operations,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("injected-persist-failure");
    }

    private sealed class CachedMetricSource : IMetricSnapshotObservationSource
    {
        private int activeSubscriptions;

        internal int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);

        public IDisposable AcquireSubscription(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
            Interlocked.Increment(ref activeSubscriptions);
            return new CallbackLease(() =>
                Interlocked.Decrement(ref activeSubscriptions));
        }

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
            => null;
    }

    private sealed class CachedResourceSource : IResourceBreakdownObservationSource
    {
        private int activeSubscriptions;
        private ResourceBreakdownSnapshot snapshot = new(
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            [])
        {
            Sampling = new ResourceBreakdownSamplingState(
                ResourceBreakdownSamplingStatuses.Warming,
                0,
                null,
                null,
                null,
                null)
        };

        internal int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);

        public IDisposable AcquireSubscription(
            string subscriptionId,
            ResourceBreakdownSampleRequest request,
            TimeSpan refreshInterval)
        {
            Interlocked.Increment(ref activeSubscriptions);
            return new CallbackLease(() =>
                Interlocked.Decrement(ref activeSubscriptions));
        }

        public ResourceBreakdownSnapshot ReadLatest(
            ResourceBreakdownSampleRequest request)
            => Volatile.Read(ref snapshot);

        internal void Publish(ResourceBreakdownSnapshot value)
            => Volatile.Write(ref snapshot, value);
    }

    private sealed class PausedResourceSource(CachedResourceSource inner)
        : IResourceBreakdownObservationSource, IDisposable
    {
        private readonly ManualResetEventSlim resume = new(false);
        private readonly TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pauseNext;
        private int readWhilePaused;

        internal Task ReadPaused => paused.Task;
        internal bool ReadWhilePaused => Volatile.Read(ref readWhilePaused) != 0;
        internal void PauseNextRead() => Volatile.Write(ref pauseNext, 1);
        internal void Release() => resume.Set();
        public void Dispose() => resume.Dispose();

        public IDisposable AcquireSubscription(string subscriptionId,
            ResourceBreakdownSampleRequest request, TimeSpan refreshInterval)
            => inner.AcquireSubscription(subscriptionId, request, refreshInterval);

        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
        {
            var snapshot = inner.ReadLatest(request);
            if (Interlocked.Exchange(ref pauseNext, 0) != 0)
            {
                paused.SetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The controlled cache read was not released.");
                }
            }
            else if (paused.Task.IsCompleted && !resume.IsSet)
            {
                Volatile.Write(ref readWhilePaused, 1);
            }
            return snapshot;
        }
    }

    private sealed class UnavailableInterruptSource : ISystemInterruptSnapshotSource
    {
        private int activeSubscriptions;
        private readonly SystemInterruptSnapshot snapshot = new(
            DateTimeOffset.UnixEpoch,
            false,
            0,
            TimeSpan.Zero,
            1,
            0,
            0,
            null,
            null,
            0,
            0,
            0,
            [],
            [],
            new SystemInterruptProviderState(
                "test",
                "Unavailable",
                "Unavailable in owner test."));

        internal int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);

        public IDisposable AcquireSubscription(
            string subscriptionId,
            TimeSpan refreshInterval)
        {
            Interlocked.Increment(ref activeSubscriptions);
            return new CallbackLease(() =>
                Interlocked.Decrement(ref activeSubscriptions));
        }

        public SystemInterruptSnapshot Read()
            => snapshot;
    }

    private sealed class CallbackLease(Action release) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                release();
            }
        }
    }

    private sealed class EmptyProtectionService : IOptimizationProtectionService
    {
        public int ProtectedTargetCount => 0;

        public Task<ProtectedOptimizationTarget?> ProtectReportAsync(
            OptimizationReportItem report,
            CancellationToken cancellationToken)
            => Task.FromResult<ProtectedOptimizationTarget?>(null);

        public Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProtectedOptimizationTarget>>([]);

        public Task<IReadOnlyList<ProtectedOptimizationTarget>> RefreshProtectedTargetsAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProtectedOptimizationTarget>>([]);

        public Task<bool> RemoveProtectedTargetAsync(
            string protectionId,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
            string protectionId,
            int protectionLevel,
            CancellationToken cancellationToken)
            => Task.FromResult<ProtectedOptimizationTarget?>(null);

        public Task<bool> IsTargetProtectedAsync(
            OptimizationReportTarget target,
            CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
