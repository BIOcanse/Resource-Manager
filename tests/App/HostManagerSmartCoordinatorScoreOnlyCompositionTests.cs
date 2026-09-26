using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.MemoryCleanup;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.PublicResources;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Adapter.SharedMemory;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EquivalentTransactionAuthorityPlansRefreshHostPlanBindingWithoutRecreate()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false);
        var journalBefore = ReadPrivateReference(
            fixture.NativeTransactions,
            "journal");
        var ownerBefore = ReadPrivateReference(
            fixture.NativeTransactions.AppliedOwnership,
            "owner");
        var journalPlanBefore = fixture.NativeTransactions.CurrentJournalPlan;
        var ownershipPlanBefore = fixture.NativeTransactions.AppliedOwnership.CurrentPlan;

        var expectedHostPlan = journalPlanBefore.HostPlan with
        {
            PlanEpoch = checked(journalPlanBefore.HostPlan.PlanEpoch + 1),
            PlanSha256 = new string('A', 64)
        };
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = checked(fixture.RuntimePlan.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "transaction-authority-plan-binding-test",
            HostManager = expectedHostPlan
        });

        Assert.NotSame(expectedHostPlan, journalPlanBefore.HostPlan);
        Assert.NotSame(expectedHostPlan, ownershipPlanBefore.HostPlan);
        Assert.True(journalPlanBefore.HasSameModuleConfiguration(
            new HostManagerTransactionJournalRuntimePlan(
                expectedHostPlan,
                expectedHostPlan.BuildSpecialize.TransactionJournalAbiVersion,
                expectedHostPlan.HostRecreate.TransactionJournal,
                expectedHostPlan.HotPublish.TransactionJournal)));
        Assert.True(ownershipPlanBefore.HasSameModuleConfiguration(
            new HostManagerAppliedOwnershipRuntimePlan(
                expectedHostPlan,
                expectedHostPlan.BuildSpecialize.AppliedOwnershipAbiVersion,
                expectedHostPlan.HostRecreate.AppliedOwnership,
                expectedHostPlan.HotPublish.AppliedOwnership)));

        await fixture.NativeTransactions.EnsureReadyAsync(CancellationToken.None);

        Assert.Same(
            expectedHostPlan,
            fixture.NativeTransactions.CurrentJournalPlan.HostPlan);
        Assert.Same(
            expectedHostPlan,
            fixture.NativeTransactions.AppliedOwnership.CurrentPlan.HostPlan);
        Assert.Same(
            journalBefore,
            ReadPrivateReference(fixture.NativeTransactions, "journal"));
        Assert.Same(
            ownerBefore,
            ReadPrivateReference(
                fixture.NativeTransactions.AppliedOwnership,
                "owner"));
    }

    [Fact]
    public async Task ColdScoreOnlyCycleCreatesNoWorkspaceJournalOrDurableSessionState()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: false);
        var durableBefore = CaptureDurableFiles(fixture.Root);
        var deploymentBefore = fixture.DeploymentState.Snapshot;

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        Assert.Equal(
            deploymentBefore with { CapturedAtUtc = default },
            fixture.DeploymentState.Snapshot with { CapturedAtUtc = default });
        Assert.Equal(0, fixture.StateStore.ReserveCalls);
        Assert.Equal(0, fixture.StateStore.SaveCalls);
        Assert.Equal(
            HostManagerTransactionJournalRuntimeState.Initializing,
            fixture.NativeTransactions.JournalState);
        Assert.Null(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
            fixture.Coordinator,
            "nativeWorkspace"));
        Assert.Equal(0UL, fixture.StateStore.Current.NativeHostSessionIncarnation);
        fixture.AssertNoEffectCollaboratorCalls();
    }

    [Fact]
    public async Task ColdExecuteReservesExactlyOnceBeforePublishingWorkspace()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        fixture.StateStore.OnReserve = () =>
        {
            Assert.Equal(1, fixture.StateStore.ReserveCalls);
            Assert.Equal(0, fixture.StateStore.SaveCalls);
            Assert.Null(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace"));
        };

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, fixture.StateStore.ReserveCalls);
        Assert.Equal<ulong>(1, fixture.StateStore.Current.NativeHostSessionIncarnation);
        Assert.Equal(
            1UL,
            ReadPrivateValue<ulong>(fixture.Coordinator, "nativeHostSessionIncarnation"));
        Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
            fixture.Coordinator,
            "nativeWorkspace"));
    }

    [Fact]
    public async Task FailedRecreateBurnsReservationAndRetryUsesNextIncarnation()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false);
        fixture.PublishSmartCoordinatorRecreatePlan();
        fixture.Coordinator.TransitionProbe = new ThrowOnceTransitionProbe(
            HostManagerSmartCoordinatorTransitionPoint.BeforeReplacementCreate);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, fixture.StateStore.ReserveCalls);
        Assert.Equal<ulong>(2, fixture.StateStore.Current.NativeHostSessionIncarnation);
        Assert.Equal(
            1UL,
            ReadPrivateValue<ulong>(fixture.Coordinator, "nativeHostSessionIncarnation"));

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, fixture.StateStore.ReserveCalls);
        Assert.Equal<ulong>(3, fixture.StateStore.Current.NativeHostSessionIncarnation);
        Assert.Equal(
            3UL,
            ReadPrivateValue<ulong>(fixture.Coordinator, "nativeHostSessionIncarnation"));
    }

    [Fact]
    public async Task WarmScoreOnlyCycleCallsNoPrepareApplySettleOrRestore()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        await fixture.PreparePendingTransactionAsync();
        fixture.StateStore.Current = fixture.StateStore.Current with
        {
            AppliedPlacements = [CreateLegacyPlacementReceipt()]
        };
        var durableBefore = CaptureDurableFiles(fixture.Root);
        var deploymentBefore = fixture.DeploymentState.Snapshot;

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        fixture.AssertNoEffectCollaboratorCalls();

        fixture.StateStore.Current = HostManagerRollbackStateDocument.Empty with
        {
            NativeHostSessionIncarnation = 1
        };
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        Assert.True(fixture.MetricSampler.ReadLatestCalls > 0);
        Assert.True(fixture.ProcessFacts.ReadLatestCalls > 0);
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        Assert.Equal(
            deploymentBefore with { CapturedAtUtc = default },
            fixture.DeploymentState.Snapshot with { CapturedAtUtc = default });
        Assert.False(File.Exists(fixture.MemoryCleanupJournalPath));
        Assert.True(fixture.RuntimePlan.OptimizationMode.AutomaticMemoryCleanupEnabled);
        Assert.True(fixture.ControlZones.CanRun(
            HostManagerSmartControlZoneIds.PolicyExecution));
        Assert.True(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.Equal(1UL, fixture.Workspace.Snapshot.CycleSequence);
        Assert.Equal(
            HostManagerTransactionJournalRuntimeState.ReadyForExecution,
            fixture.NativeTransactions.JournalState);
        fixture.AssertNoEffectCollaboratorCalls();
    }

    [Fact]
    public async Task ForegroundCyclePublishesItsVersionedWakeDeadline()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var deadline = ReadPrivateField<HostManagerVersionedWakeDeadline>(
            fixture.Coordinator,
            "schedulingWakeDeadline");
        Assert.NotNull(deadline);
        Assert.Equal(default, deadline.Snapshot);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var publication = deadline.Snapshot;
        Assert.Equal(1UL, publication.Version);
        Assert.True(publication.HasDeadline);
        Assert.True(publication.DeadlineTimestamp > 0);
    }

    [Fact]
    public async Task ScoreOnlyCycle_CpuScoringDoesNotRequireCurrentMemoryMetrics()
    {
        const int processId = 4_241;
        const ulong processStartKey = 132_537_599_900_000_000;
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:cpu-only",
            baseScore: 100,
            cpuUsagePercent: 50,
            memoryUsagePercent: 0);
        processFacts = processFacts with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage
                | SchedulingProcessMetricMask.RuntimeState,
            Processes =
            [
                processFacts.Processes[0] with
                {
                    ValidMetricMask = SchedulingProcessMetricMask.CpuUsage
                        | SchedulingProcessMetricMask.RuntimeState
                }
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: false);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var row = Assert.Single(
            fixture.Workspace.CurrentSnapshotRows.ToArray(),
            candidate => candidate.ProcessId == processId
                && candidate.ProcessStartKey == processStartKey);
        Assert.Equal(50, row.CpuOccupancyPercent);
        Assert.True(row.CpuScore > 0);
    }

    [Fact]
    public async Task MixedCpuAvailability_PublishesReadyMemoryAuthorityForScoredProcessesOnly()
    {
        const int scoredProcessId = 4_242;
        const int unscoredProcessId = 4_243;
        var processFacts = CreateCompleteProcessFacts(
            (
                scoredProcessId,
                132_537_599_910_000_000,
                "software:mixed-cpu:scored",
                35D,
                40D,
                25D),
            (
                unscoredProcessId,
                132_537_599_920_000_000,
                "software:mixed-cpu:unscored",
                35D,
                0D,
                25D));
        processFacts = processFacts with
        {
            Processes =
            [
                processFacts.Processes[0],
                processFacts.Processes[1] with
                {
                    ValidMetricMask = SchedulingProcessMetricMask.RuntimeState
                }
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: true,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false);

        _ = await fixture.RunRealtimeCycleAsync();

        var authority = fixture.Coordinator.SchedulingAuthority;
        Assert.True(
            authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready,
            $"Expected Ready but got {authority.Availability}: {authority.UnavailableReason}");
        Assert.NotNull(authority.MemoryModes);
        var projection = Assert.IsType<HostManagerNonAdaptedMemoryModeProjectionSnapshot>(
            fixture.Coordinator.NonAdaptedMemoryModeProjection);
        var directive = Assert.Single(projection.Processes);
        Assert.Equal(scoredProcessId, directive.ProcessId);
        Assert.DoesNotContain(
            projection.Processes,
            candidate => candidate.ProcessId == unscoredProcessId);
    }

    [Fact]
    public async Task DefaultGameAndOtherNeverPlanPagedFrozenOrLevel4()
    {
        const int processId = 4_242;
        const ulong processStartKey = 132_537_599_910_000_000;
        const string softwareId = "software:paired-kind";
        var cases = new[]
        {
            (
                SoftwareKinds.Game,
                NativeSmartCoordinatorSoftwareKind.Game,
                95D,
                NativeMemoryMode.Normal),
            (
                SoftwareKinds.Other,
                NativeSmartCoordinatorSoftwareKind.GeneralApplication,
                35D,
                NativeMemoryMode.Optimize)
        };

        foreach (var (
            kind,
            expectedNativeKind,
            expectedBaseScore,
            expectedMemoryMode) in cases)
        {
            var processFacts = CreateCompleteProcessFacts(
                processId,
                processStartKey,
                softwareId,
                OptimizationRuntimeScoringDefaults.BaseScoreForKind(kind),
                cpuUsagePercent: 1,
                memoryUsagePercent: 25);
            processFacts = processFacts with
            {
                Processes =
                [
                    processFacts.Processes[0] with
                    {
                        SoftwareKind = kind,
                        SoftwareDisplayKind = kind
                    }
                ]
            };
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true,
                processFactsSnapshot: processFacts,
                policyExecutionEnabled: true,
                memoryModePolicyEnabled: true,
                automaticMemoryCleanupEnabled: false,
                cpuUsagePercent: 99);
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: 100,
                cpuUsagePercent: 99));
            var durableBefore = CaptureDurableFiles(fixture.Root);

            for (var cycle = 0; cycle < 4; cycle++)
            {
                await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            }

            var identity = Assert.Single(
                fixture.Workspace.InputRows.ToArray(),
                row => row.StructSize != 0
                    && row.MetricKind == NativeSmartCoordinatorMetricKind.None
                    && row.ProcessId == processId
                    && row.ProcessStartKey == processStartKey);
            Assert.Equal(expectedNativeKind, identity.SoftwareKind);
            Assert.Equal(expectedBaseScore, identity.BaseScore);
            var process = Assert.Single(
                fixture.Workspace.CurrentSnapshotRows.ToArray(),
                row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
                    && row.ProcessId == processId
                    && row.ProcessStartKey == processStartKey);
            Assert.Equal(expectedNativeKind, process.SoftwareKind);
            Assert.Equal(expectedBaseScore, process.BaseScore);
            Assert.NotEqual(NativeSmartCoordinatorProcessGrade.Level4, process.DesiredProcessGrade);
            var directive = Assert.Single(
                fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
            Assert.Equal(expectedMemoryMode, directive.Mode);
            Assert.NotEqual(NativeMemoryMode.PagedFrozen, directive.Mode);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ActionCount);
            Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
            fixture.AssertNoEffectCollaboratorCalls();
        }
    }

    [Fact]
    public async Task ScoreOnlyCycleAt256ProcessBoundaryScoresExactInventoryWithoutEffects()
    {
        const int processCapacity = 256;
        var processFacts = CreateScaleProcessFacts(processCapacity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: processFacts,
            smartCoordinatorMaximumProcesses: processCapacity);
        var durableBefore = CaptureDurableFiles(fixture.Root);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var snapshot = fixture.Workspace.Snapshot;
        Assert.True(snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.Equal((uint)processCapacity, snapshot.ProcessCount);
        Assert.Equal((uint)processCapacity, snapshot.SoftwareCount);
        Assert.Equal(checked((uint)(2 * processCapacity)), snapshot.SnapshotRowCount);
        Assert.Equal(0U, snapshot.ActionCount);
        var processRows = fixture.Workspace.CurrentSnapshotRows
            .ToArray()
            .Where(static row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process)
            .ToArray();
        Assert.Equal(processCapacity, processRows.Length);
        Assert.Equal(
            processCapacity,
            processRows
                .Select(static row => (row.ProcessId, row.ProcessStartKey))
                .Distinct()
                .Count());
        Assert.All(processRows, static row => Assert.True(row.CpuScore > 0));
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        fixture.AssertNoEffectCollaboratorCalls();
    }

    [Fact]
    public async Task ScoreOnlyCycleRejectsCapacityPlusOneExactInventoryWithoutEffects()
    {
        const int processCapacity = 256;
        var processFacts = CreateScaleProcessFacts(processCapacity + 1);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: processFacts,
            smartCoordinatorMaximumProcesses: processCapacity);
        var durableBefore = CaptureDurableFiles(fixture.Root);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Coordinator.RunOnceAsync(CancellationToken.None));

        Assert.Contains("capacity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        Assert.Equal(0U, fixture.Workspace.PlannedActionCount);
        fixture.AssertNoEffectCollaboratorCalls();
    }

    [Fact]
    public async Task PerformanceDiagnostics_CompletedCyclePublishesStructuredRecord()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            performanceLogEnabled: true);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var record = Assert.Single(fixture.DebugLogWriter.Records);
        Assert.Equal("smart-optimization", record.Category);
        Assert.Equal("cycle-performance", record.EventName);
        Assert.Equal("completed", record.Properties["outcome"]);
        Assert.Matches(
            "^[0-9a-f]{32}$",
            Assert.IsType<string>(record.Properties["producerInstanceId"]));
        Assert.Equal(1L, Assert.IsType<long>(record.Properties["cycleSequence"]));
        Assert.IsType<DateTimeOffset>(record.Properties["cycleStartedAtUtc"]);
        Assert.InRange(
            Assert.IsType<long>(record.Properties["cycleStartedAtQpcTicks"]),
            1,
            long.MaxValue);
        Assert.InRange(
            Assert.IsType<long>(record.Properties["cycleCompletedAtQpcTicks"]),
            1,
            long.MaxValue);
        Assert.InRange(
            Assert.IsType<long>(record.Properties["qpcFrequency"]),
            1,
            long.MaxValue);
        Assert.NotEmpty(Assert.IsType<Dictionary<string, object?>[]>(
            record.Properties["phases"]));
        Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.IsType<Dictionary<string, object?>>(record.Properties["guards"]);
    }

    [Fact]
    public async Task PerformanceDiagnostics_RejectedFeedbackIsNotCountedAsApplied()
    {
        const int processId = 4_245;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:diagnostics-rejected-feedback",
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            automaticMemoryCleanupEnabled: false,
            performanceLogEnabled: true,
            cpuUsagePercent: 99);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            if (cycle != 0)
            {
                fixture.PublishNextHostedCpuObservation();
            }
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        }

        var record = fixture.DebugLogWriter.Records[^1];
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(1U, Assert.IsType<uint>(details["nativeFeedbackCount"]));
        Assert.Equal(1, Assert.IsType<int>(counts["policyChanges"]));
        Assert.Equal(0, Assert.IsType<int>(counts["appliedTargets"]));
        Assert.Equal(0, Assert.IsType<int>(counts["changedCount"]));
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task ExecuteCycle_ReusedPidRetiresOnlyOldOwnershipAndClearsAuthoritativeGate()
    {
        const int processId = 4_242;
        const ulong oldProcessStartKey = 132_537_600_000_000_000;
        const ulong replacementProcessStartKey = oldProcessStartKey + 10_000_000;
        const string softwareId = "software:pid-reuse";
        var processFacts = CreateProcessFacts(
            processId,
            replacementProcessStartKey,
            softwareId);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        DateTimeOffset.FromFileTime(
                            checked((long)replacementProcessStartKey))));
            },
            policyExecutionEnabled: false);
        var oldProcess = await fixture.SeedProcessOwnershipAsync(
            processId,
            oldProcessStartKey,
            softwareId,
            memoryPolicy: false,
            actionId: 101);
        var oldMemory = await fixture.SeedProcessOwnershipAsync(
            processId,
            oldProcessStartKey,
            softwareId,
            memoryPolicy: true,
            actionId: 102);
        var replacement = await fixture.SeedProcessOwnershipAsync(
            processId,
            replacementProcessStartKey,
            softwareId,
            memoryPolicy: false,
            actionId: 103);

        Assert.Equal(
            3,
            (await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
                CancellationToken.None)).Records.Count);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        var remaining = Assert.Single(ownership.Records);
        Assert.Equal(replacement.Primary, remaining.Primary);
        Assert.False(File.Exists(oldProcess.PayloadPath));
        Assert.False(File.Exists(oldMemory.PayloadPath));
        Assert.True(File.Exists(replacement.PayloadPath));
        Assert.Equal(2, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(2, fixture.ProcessPolicyWriter.TotalCalls);
        Assert.False(ReadPrivateValue<bool>(
            fixture.Coordinator,
            "authoritativeAppliedFactsRequired"));
        Assert.False(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.RequiresAuthoritativeResync));
        var rows = fixture.Workspace.CurrentSnapshotRows;
        var replacementRow = Assert.Single(
            rows.ToArray(),
            row => row.ProcessId == processId &&
                row.ProcessStartKey == replacementProcessStartKey);
        Assert.True(replacementRow.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned));
    }

    [Fact]
    public async Task RepeatedRawMemoryPressure_DoesNotClaimCompletedNormalReleaseRounds()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(
                24_101, 132_537_599_900_000_000, "software:raw-memory-pressure",
                baseScore: 30, cpuUsagePercent: 0, memoryUsagePercent: 25),
            processRecoveryRead: processId =>
            {
                Assert.Equal(24_101, processId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId, DateTimeOffset.FromFileTime(132_537_599_900_000_000)));
            },
            scoreOnlyEnabled: false,
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true);

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        var first = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        _ = await fixture.RunRealtimeCycleAsync();

        var shortage = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        Assert.True(first.MemoryCurrentShortage);
        Assert.Equal(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            shortage.Memory.State);
        Assert.True(shortage.MemoryCurrentShortage);
        Assert.False(shortage.VideoMemoryCurrentShortage);
        Assert.False(shortage.MemoryAfterNormalReleaseRounds);
        Assert.False(shortage.VideoMemoryAfterNormalReleaseRounds);
        Assert.True(
            fixture.MemoryCleanupPlanner.PlanCalls == 1,
            fixture.DescribeMemoryCleanupState());
    }

    [Fact]
    public async Task RepeatedHealthyMemorySample_CannotAuthorizeAnotherRecallCycle()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: false);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 20));

        _ = await fixture.RunRealtimeCycleAsync();
        var first = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        _ = await fixture.RunRealtimeCycleAsync();
        var repeated = fixture.PublicResourceManager.LastRequest!.Value.Shortage;

        Assert.True(first.Memory.RecallAllowed);
        Assert.Equal(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            repeated.Memory.State);
        Assert.False(repeated.Memory.RecallAllowed);
    }

    [Fact]
    public async Task TwoKnownMemoryReleaseRoundsAndNewerSamples_EnablePublicPressureGate()
    {
        const int processId = 24_200;
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:normal-release-proof",
            baseScore: 30,
            cpuUsagePercent: 0,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            timeProvider: time);
        fixture.MemoryCleanupPlanner.PlanHandler = request =>
            CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests
            .Select(request =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(processId, request.ProcessId);
                Assert.True(request.TrimWorkingSet);
                return new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.TrimWorkingSet,
                        Succeeded: true,
                        "trimmed")]);
            })
            .ToArray();

        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            committedGeneration: 1,
            capturedAt: time.GetUtcNow()));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.False(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);

        time.Advance(TimeSpan.FromSeconds(1));
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            committedGeneration: 2,
            capturedAt: time.GetUtcNow()));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.False(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);

        time.Advance(TimeSpan.FromSeconds(1));
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            committedGeneration: 3,
            capturedAt: time.GetUtcNow()));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.False(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);

        time.Advance(TimeSpan.FromSeconds(1));
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            committedGeneration: 4,
            capturedAt: time.GetUtcNow()));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);
        Assert.Equal(0U, fixture.PublicResourceManager.NewEffectAttemptCount);

        time.Advance(TimeSpan.FromSeconds(1));
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            committedGeneration: 5,
            capturedAt: time.GetUtcNow()));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);
        Assert.Equal(1U, fixture.PublicResourceManager.NewEffectAttemptCount);
        Assert.Equal(4, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(3, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task UnavailableMemoryCapacity_DoesNotRunCleanupOrClaimShortage()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            memoryUsageAvailable: false);
        fixture.MemoryCleanupPlanner.PlanHandler = request =>
            CreateAutomaticMemoryCleanupPlan(request);

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        var shortage = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        Assert.Equal(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            shortage.Memory.State);
        Assert.False(shortage.MemoryCurrentShortage);
        Assert.False(shortage.MemoryAfterNormalReleaseRounds);
    }

    [Fact]
    public async Task RetainedMemoryCapacity_DoesNotRunCleanupOrAllowRecall()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(
                24_102, 132_537_599_900_000_000, "software:retained-memory-capacity",
                baseScore: 30, cpuUsagePercent: 0, memoryUsagePercent: 25),
            scoreOnlyEnabled: false,
            policyExecutionEnabled: true,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: true);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 20,
            memoryObservationStatus: SamplingObservationStatus.RetainedLastGood));
        fixture.MemoryCleanupPlanner.PlanHandler = request =>
            CreateAutomaticMemoryCleanupPlan(request);

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            fixture.Coordinator.SchedulingAuthority.Availability);
        Assert.Null(fixture.Coordinator.SchedulingAuthority.MemoryModes);
        Assert.Null(fixture.Coordinator.NonAdaptedMemoryModeProjection);
        var shortage = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        Assert.Equal(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            shortage.Memory.State);
        Assert.False(shortage.Memory.RecallAllowed);
        Assert.False(shortage.MemoryAfterNormalReleaseRounds);
    }

    [Fact]
    public async Task MissingHostedSnapshot_RevokesPreviouslyReadySchedulingAuthority()
    {
        const int processId = 24_219;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:authority-loss",
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: true,
            processFactsSnapshot: processFacts,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false);

        _ = await fixture.RunRealtimeCycleAsync();
        var ready = fixture.Coordinator.SchedulingAuthority;
        Assert.True(
            ready.Availability == HostManagerSchedulingAuthorityAvailability.Ready,
            $"Expected Ready but got {ready.Availability}: {ready.UnavailableReason}");

        fixture.MetricSampler.ObservationAvailable = false;
        _ = await fixture.RunRealtimeCycleAsync();

        var unavailable = fixture.Coordinator.SchedulingAuthority;
        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.Unavailable,
            unavailable.Availability);
        Assert.True(unavailable.AttemptGeneration > ready.AttemptGeneration);
        Assert.Equal(
            "hardware-snapshot-unavailable",
            unavailable.UnavailableReason);
        Assert.Null(unavailable.Compute);
        Assert.Null(unavailable.MemoryModes);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_StopsWhenPressureRecovers()
    {
        const int processId = 24_220;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            startKey,
            "software:final-pressure",
            baseScore: 20,
            cpuUsagePercent: 1,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: 20,
                committedGeneration: 2,
                capturedAt: DateTimeOffset.UtcNow.AddTicks(1)));

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 0);
        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(69, 0)]
    [InlineData(70, 0)]
    [InlineData(71, 1)]
    [InlineData(100, 1)]
    [InlineData(101, 0)]
    public async Task AutomaticMemoryCleanupFinalAdmission_UsesStrictGuardedBoundary(
        double finalMemoryUsagePercent,
        int expectedPlanCalls)
    {
        const int processId = 24_221;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:strict-guarded-boundary",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler = static _ =>
            AutomaticMemoryCleanupPlanResult.Empty;
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: finalMemoryUsagePercent,
                committedGeneration: 2,
                capturedAt: DateTimeOffset.UtcNow.AddTicks(1)));

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(expectedPlanCalls, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        if (expectedPlanCalls == 0)
        {
            return;
        }

        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        Assert.Equal(
            AutomaticMemoryCleanupRequestKind.Normal
                | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
            request.Kind);
        Assert.Equal(
            1 - finalMemoryUsagePercent / 100,
            request.OrdinaryMemoryFreeRatio,
            precision: 12);
        Assert.Equal(
            request.OrdinaryMemoryFreeRatio,
            request.PhysicalMemoryFreeRatio,
            precision: 12);
        Assert.Equal(0.5, request.VirtualMemoryFreeRatio, precision: 12);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupProductionFinalAdmissionPublishesExactLineage()
    {
        const int processId = 24_224;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var baselineCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var finalCapturedAt = baselineCapturedAt.AddSeconds(1);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:final-lineage",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 99,
            committedGeneration: 1,
            capturedAt: baselineCapturedAt));
        fixture.MetricSampler.AdvanceMemoryOnlyCapture = false;
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: 80,
                committedGeneration: 2,
                capturedAt: finalCapturedAt));
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();
        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(
            AutomaticMemoryCleanupRequestKind.Normal
                | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
            request.Kind);
        Assert.Equal(0.2, request.OrdinaryMemoryFreeRatio, precision: 12);
        Assert.Equal(0.2, request.PhysicalMemoryFreeRatio, precision: 12);
        Assert.Equal(0.5, request.VirtualMemoryFreeRatio, precision: 12);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_MissingProcessMemoryIsFilteredNotZeroFilled()
    {
        const int completeProcessId = 24_230;
        const int missingProcessId = 24_231;
        var completeStartedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        var missingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processFacts = CreateCompleteProcessFacts(
            (
                completeProcessId,
                checked((ulong)completeStartedAt.ToFileTime()),
                "software:complete-final-memory",
                20,
                10,
                25),
            (
                missingProcessId,
                checked((ulong)missingStartedAt.ToFileTime()),
                "software:missing-final-memory",
                20,
                10,
                25));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: processId => processId switch
            {
                completeProcessId =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(
                            completeProcessId,
                            completeStartedAt)),
                missingProcessId =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(
                            missingProcessId,
                            missingStartedAt)),
                _ => throw new InvalidOperationException(
                    "Unexpected process recovery read.")
            },
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler = static _ =>
            AutomaticMemoryCleanupPlanResult.Empty;

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);

        var finalHardware = CreateHardwareSnapshot(
            memoryUsagePercent: 80,
            committedGeneration: 2,
            capturedAt: DateTimeOffset.UtcNow.AddSeconds(1));
        fixture.MetricSampler.SetSnapshot(finalHardware);
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            processFacts,
            finalHardware,
            process => process.ProcessId == completeProcessId
                ? process with { MemoryUsagePercent = 37 }
                : process with
                {
                    ValidMetricMask = process.ValidMetricMask
                        & ~SchedulingProcessMetricMask.MemoryUsage,
                    MemoryUsagePercent = 0
                }));

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(
            fixture.MemoryCleanupPlanner.PlanCalls == 1,
            fixture.DescribeMemoryCleanupState());
        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        var complete = Assert.Single(
            request.Candidates,
            candidate => candidate.ProcessId == completeProcessId);
        Assert.True(complete.CanApply);
        Assert.Equal(37, complete.MemoryUsedPercent);
        Assert.DoesNotContain(
            request.Candidates,
            candidate => candidate.ProcessId == missingProcessId);
        Assert.Contains(
            fixture.ProcessFacts.ReadLatestRequests,
            static request => request.RequestedMetricMask ==
                    (SchedulingProcessMetricMask.MemoryUsage
                        | SchedulingProcessMetricMask.RuntimeState)
                && request.ExpectedMemoryUsageDependency is not null);
    }

    [Theory]
    [InlineData(95, 50, false, 0.05, 1)]
    [InlineData(20, 93, true, 0.8, 0.07)]
    public async Task AutomaticMemoryCleanupProductionFinalAdmission_ReachesOneEmergencyMode(
        double finalMemoryUsagePercent,
        double finalVirtualMemoryUsagePercent,
        bool finalVirtualMemoryAvailable,
        double expectedPhysicalFreeRatio,
        double expectedVirtualFreeRatio)
    {
        const int processId = 24_227;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:emergency-final-admission",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: finalMemoryUsagePercent,
                virtualMemoryUsagePercent: finalVirtualMemoryUsagePercent,
                virtualMemoryUsageAvailable: finalVirtualMemoryAvailable,
                committedGeneration: 2,
                capturedAt: DateTimeOffset.UtcNow.AddTicks(1)));
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        Assert.Equal(
            AutomaticMemoryCleanupRequestKind.Normal
                | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
            request.Kind);
        Assert.Equal(
            expectedPhysicalFreeRatio,
            request.OrdinaryMemoryFreeRatio,
            precision: 12);
        Assert.Equal(
            expectedPhysicalFreeRatio,
            request.PhysicalMemoryFreeRatio,
            precision: 12);
        Assert.Equal(
            expectedVirtualFreeRatio,
            request.VirtualMemoryFreeRatio,
            precision: 12);
    }

    [Fact]
    public async Task ValidationScopeBlocksAutomaticCleanupAndPublicResourceLifecycle()
    {
        const int processId = 24_290;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            processId,
            startKey));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:scoped-memory-isolation",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            processEffectValidationScopeAuthority: validation.Authority);
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.PublicResourceManager.TickAttemptCalls);
        Assert.Null(fixture.PublicResourceManager.LastRequest);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupProductionFinalAdmission_MissingVirtualFactUsesPhysicalPressureOnly()
    {
        const int processId = 24_228;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:missing-virtual-final-admission",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                memoryUsagePercent: 80,
                virtualMemoryUsageAvailable: false,
                committedGeneration: 2,
                capturedAt: DateTimeOffset.UtcNow.AddTicks(1)));
        fixture.MemoryCleanupPlanner.PlanHandler = static _ =>
            AutomaticMemoryCleanupPlanResult.Empty;

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.MemoryCleanupPlanner.PlanCalls);
        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        Assert.Equal(0.2, request.PhysicalMemoryFreeRatio, precision: 12);
        Assert.Equal(1, request.VirtualMemoryFreeRatio, precision: 12);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("memory")]
    public async Task AutomaticMemoryCleanupFinalAdmission_RejectsStaleFinalSample(
        string sampleKind)
    {
        const int processId = 24_225;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:stale-final-sample",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        await fixture.PrimeMemoryCleanupAdmissionAsync(
            publishHardware: sampleKind != "memory",
            publishProcessMemory: sampleKind != "process");
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 0);
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("overflow")]
    public async Task AutomaticMemoryCleanupFinalAdmission_RejectsIncompleteProcessInventory(
        string incompleteKind)
    {
        const int processId = 24_229;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            startKey,
            "software:incomplete-final-inventory",
            baseScore: 20,
            cpuUsagePercent: 1,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.PublicResourceManager.TickHandler = () =>
        {
            var current = processFacts.Processes[0] with { SourceGeneration = 2 };
            fixture.ProcessFacts.SetSnapshot(processFacts with
            {
                Generation = 2,
                ObservedAtUtcTicks = processFacts.ObservedAtUtcTicks + 1,
                EnumeratedCount = 2,
                EmittedCount = 1,
                SkippedCount = incompleteKind == "skipped" ? 1U : 0U,
                OverflowCount = incompleteKind == "overflow" ? 1U : 0U,
                Processes = [current]
            });
        };

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 0);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_RechecksCurrentPolicyZone()
    {
        const int processId = 24_226;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:final-policy-zone",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.PublicResourceManager.TickHandler = () =>
            fixture.ControlZones.PolicyExecutionEnabled = false;

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 0);
    }

    [Theory]
    [InlineData("high-base-score")]
    [InlineData("adapted")]
    [InlineData("process-exited")]
    public async Task AutomaticMemoryCleanupFinalAdmission_StopsForCurrentProcessFacts(
        string transition)
    {
        const int processId = 24_221;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            processId,
            startKey,
            "software:final-process-facts",
            baseScore: 20,
            cpuUsagePercent: 1,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.PublicResourceManager.TickHandler = () =>
        {
            var current = processFacts.Processes[0] with { SourceGeneration = 2 };
            var transitioned = transition switch
            {
                "high-base-score" => processFacts with
                {
                    Generation = 2,
                    Processes = [current with { BaseScore = 81 }]
                },
                "adapted" => processFacts with
                {
                    Generation = 2,
                    Processes =
                    [
                        current with
                        {
                            SoftwareKind = SoftwareKinds.Adapted,
                            SoftwareDisplayKind = "Adapted"
                        }
                    ]
                },
                "process-exited" => processFacts with
                {
                    Generation = 2,
                    EnumeratedCount = 0,
                    EmittedCount = 0,
                    Processes = []
                },
                _ => throw new InvalidOperationException("Unknown process-fact transition.")
            };
            fixture.ProcessFacts.SetSnapshot(transitioned with
            {
                ObservedAtUtcTicks = processFacts.ObservedAtUtcTicks + 1
            });
        };

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 0);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_StopsForLiveIdentityDrift()
    {
        const int processId = 24_222;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var recoveryReadCount = 0;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:final-identity",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(
                    processId,
                    ++recoveryReadCount == 1 ? startedAt : startedAt.AddSeconds(1))),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 1);
        Assert.Equal(2, recoveryReadCount);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_UnavailableIdentityNeverCompletesReleaseRound()
    {
        const int processId = 24_224;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var recoveryReadCount = 0;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                "software:final-identity-unavailable",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => ++recoveryReadCount % 2 == 1
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, startedAt))
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                    nativeErrorCode: 5,
                    "identity unavailable"),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();
        fixture.MetricSampler.PublishNextHostedMemoryObservation();
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 2);
        Assert.Equal(4, recoveryReadCount);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupInitialUnknownIdentity_DoesNotHideBehindPeerSuccess()
    {
        const int unknownProcessId = 24_227;
        const int liveProcessId = 24_228;
        var unknownStartedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        var liveStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processFacts = CreateCompleteProcessFacts(
            (
                unknownProcessId,
                checked((ulong)unknownStartedAt.ToFileTime()),
                "software:initial-unknown",
                20,
                1,
                25),
            (
                liveProcessId,
                checked((ulong)liveStartedAt.ToFileTime()),
                "software:initial-live",
                20,
                1,
                25));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: processId => processId == unknownProcessId
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                    nativeErrorCode: 5,
                    "identity unavailable")
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(liveProcessId, liveStartedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests
            .Select(request => new ProcessResourcePolicyBatchWriteResult(
                request.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();
        var sampleAt = DateTimeOffset.UtcNow;

        for (var generation = 1UL; generation <= 3; generation++)
        {
            fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
                committedGeneration: generation,
                capturedAt: sampleAt.AddSeconds(checked((long)generation))));
            fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                fixture.ProcessFacts.Snapshot,
                fixture.MetricSampler.Snapshot));
            _ = await fixture.RunRealtimeCycleAsync();
        }

        Assert.Equal(2, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(2, fixture.MemoryCleanupPlanner.CompleteCalls);
        Assert.Equal(2, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.False(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupFinalAdmission_StopsForNewActiveProtection()
    {
        const int processId = 24_223;
        const string softwareId = "software:final-protection";
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                softwareId,
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler =
            CreateFinalAdmissionAwareMemoryCleanupPlan;
        await fixture.PrimeMemoryCleanupAdmissionAsync();
        fixture.ProtectionService.Targets =
            [
                new ProtectedOptimizationTarget(
                    "protection:final-admission",
                    "Process",
                    HostManagerTargetIdentity.CreateProcessTargetId(processId, startKey),
                    "final protection",
                    softwareId,
                    "final protection",
                    SoftwareKinds.Other,
                    DateTimeOffset.UtcNow,
                    "test",
                    "report:test",
                    DateTimeOffset.UtcNow,
                    OptimizationProtectionStates.Active,
                    AllowsPlacementAvoidance: true,
                    ProtectionLevel: OptimizationProtectionLevels.Level2NoOptimization)
            ];

        _ = await fixture.RunRealtimeCycleAsync();

        AssertFinalAdmissionDeferredWithoutEffects(fixture, expectedPlanCalls: 1);
        Assert.Equal(3, fixture.ProtectionService.GetCalls);
    }

    [Fact]
    public async Task DisabledAutomaticCleanup_DoesNotDisableHealthyPublicResourceRecallCapacity()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: false);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 20));

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        var shortage = fixture.PublicResourceManager.LastRequest!.Value.Shortage;
        Assert.Equal(
            HostPublicResourceCapacityObservationState.HealthyFresh,
            shortage.Memory.State);
        Assert.True(shortage.Memory.RecallAllowed);
        Assert.False(shortage.MemoryAfterNormalReleaseRounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteCycle_SameIncarnationProcessAttributionDrift_UsesOwnedAnchor(bool withCpuScore)
    {
        const int processId = 4_243;
        const ulong processStartKey = 132_537_600_100_000_000;
        const string ownedSoftwareId = "software:owned";
        const string currentSoftwareId = "software:reattributed";
        var topology = CreateAutomaticPlacementTopology();
        var residency = CpuCoreResidencyTestValues.Create(
            91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(processId, processStartKey, ("core:0", 50)),
            CpuCoreResidencyTestValues.Process(processId + 1, processStartKey + 1, ("core:0", 10)),
            CpuCoreResidencyTestValues.Process(processId + 2, processStartKey + 2, ("core:0", 10)));
        var facts = CreateProcessFacts(processId, processStartKey, currentSoftwareId);
        facts = facts with
        {
            EnumeratedCount = 3,
            EmittedCount = 3,
            Processes =
            [
                facts.Processes[0],
                CreateProcessFacts(processId + 1, processStartKey + 1, currentSoftwareId).Processes[0],
                CreateProcessFacts(processId + 2, processStartKey + 2, "software:unaffected").Processes[0]
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: facts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        DateTimeOffset.FromFileTime(checked((long)processStartKey))));
            },
            policyExecutionEnabled: false,
            cpuCoreReader: withCpuScore ? new RecordingCpuCoreResidencyReader(() => residency) : null,
            cpuTopology: withCpuScore ? topology : null,
            cpuScoring: withCpuScore ? HostManagerTestPlanFactory.CreateCpuScoring(topology) : null);
        var seeded = new List<SeededProcessOwnership>
        {
            await fixture.SeedProcessOwnershipAsync(
                processId,
                processStartKey,
                ownedSoftwareId,
                memoryPolicy: false,
                actionId: 201)
        };
        WritePrivateField(
            fixture.Coordinator,
            "authoritativeAppliedFactsRequired",
            true);

        var before = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var after = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Equal(before.Header.LedgerRevision, after.Header.LedgerRevision);
        Assert.Equal(seeded.Count, after.Records.Count);
        foreach (var ownership in seeded)
        {
            Assert.Contains(
                after.Records,
                record => record.Primary.Equals(ownership.Primary));
            Assert.True(File.Exists(ownership.PayloadPath));
        }
        Assert.Equal(0, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
        Assert.False(ReadPrivateValue<bool>(
            fixture.Coordinator,
            "authoritativeAppliedFactsRequired"));
        Assert.False(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.RequiresAuthoritativeResync));
        var row = Assert.Single(
            fixture.Workspace.CurrentSnapshotRows.ToArray(),
            candidate => candidate.ProcessId == processId
                && candidate.ProcessStartKey == processStartKey);
        Assert.Equal(
            NativeStableIdentity.CreateCaseInsensitiveKey(ownedSoftwareId),
            row.SoftwareKey);
        Assert.True(row.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned));
        if (withCpuScore)
        {
            var unaffected = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
                candidate => candidate.ProcessId == processId + 2);
            Assert.True(unaffected.CpuScore > 0);
            Assert.Equal(0, row.CpuScore);
        }

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var second = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Equal(after.Header.LedgerRevision, second.Header.LedgerRevision);
        Assert.Equal(after.Records.Count, second.Records.Count);
        Assert.All(seeded, ownership => Assert.True(File.Exists(ownership.PayloadPath)));
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task ExecuteCycle_ProcessWithoutOwnershipUsesCurrentSoftwareAttribution()
    {
        const int processId = 4_244;
        const ulong processStartKey = 132_537_600_200_000_000;
        const string softwareId = "software:current";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateProcessFacts(
                processId,
                processStartKey,
                softwareId),
            policyExecutionEnabled: false);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var row = Assert.Single(
            fixture.Workspace.CurrentSnapshotRows.ToArray(),
            candidate => candidate.ProcessId == processId
                && candidate.ProcessStartKey == processStartKey);
        Assert.Equal(
            NativeStableIdentity.CreateCaseInsensitiveKey(softwareId),
            row.SoftwareKey);
        Assert.False(row.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned));
    }

    public static IEnumerable<object[]> LifecycleShutdownCases
    {
        get
        {
            foreach (var point in Enum.GetValues<HostManagerSmartCoordinatorTransitionPoint>())
            {
                yield return [(int)point, false];
                yield return [(int)point, true];
            }
        }
    }

    [Fact]
    public async Task BackgroundSchedulerUsesPersistentPointTwoHertzSnapshotsWithoutDirectCapture()
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            normalIntervalMilliseconds: 60_000,
            eventIntervalMilliseconds: 60_000,
            cpuCoreReader: cpuReader);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.MetricSampler.ReadLatestCalls == 0
                || fixture.ProcessFacts.ReadLatestCalls == 0)
            {
                await Task.Delay(10, timeout.Token);
            }

            var hardware = Assert.Single(fixture.MetricSampler.SubscriptionHistory);
            Assert.Equal(("host-manager-smart-coordinator:cpu-cores", TimeSpan.FromSeconds(5)),
                Assert.Single(cpuReader.Subscriptions));
            Assert.Equal("host-manager-smart-coordinator", hardware.SubscriptionId);
            Assert.Equal(TimeSpan.FromSeconds(5), hardware.RefreshInterval);
            Assert.False(hardware.Request.IsAll);
            Assert.True(hardware.Request.IncludesAllGpuCoreMetrics);
            Assert.True(hardware.Request.Includes("cpu.usage"));
            Assert.True(hardware.Request.Includes("memory.usage"));
            Assert.False(hardware.Request.Includes("memory.percent"));
            Assert.True(hardware.Request.Includes("virtualMemory.usage"));
            Assert.False(hardware.Request.Includes("virtualMemory.percent"));

            Assert.Equal(2, fixture.ProcessFacts.SubscriptionHistory.Count);
            var computeProcesses = Assert.Single(
                fixture.ProcessFacts.SubscriptionHistory,
                static subscription => subscription.SubscriptionId ==
                    "host-manager-smart-coordinator:compute");
            Assert.Equal(TimeSpan.FromSeconds(5), computeProcesses.RefreshInterval);
            Assert.Equal(
                SchedulingProcessMetricMask.CpuUsage
                    | SchedulingProcessMetricMask.GpuUsage
                    | SchedulingProcessMetricMask.GpuDedicatedMemory
                    | SchedulingProcessMetricMask.RuntimeState,
                computeProcesses.MetricMask);
            var memoryProcesses = Assert.Single(
                fixture.ProcessFacts.SubscriptionHistory,
                static subscription => subscription.SubscriptionId ==
                    "host-manager-smart-coordinator:memory-final-admission");
            Assert.Equal(TimeSpan.FromSeconds(5), memoryProcesses.RefreshInterval);
            Assert.Equal(
                SchedulingProcessMetricMask.MemoryUsage
                    | SchedulingProcessMetricMask.RuntimeState,
                memoryProcesses.MetricMask);

            Assert.Equal(1, fixture.MetricSampler.ActiveSubscriptionCount);
            Assert.Equal(2, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
            Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
            Assert.Contains(
                fixture.ProcessFacts.ReadLatestRequests,
                static request => request.RequestedMetricMask.HasFlag(
                        SchedulingProcessMetricMask.CpuUsage)
                    && request.RequestedMetricMask.HasFlag(
                        SchedulingProcessMetricMask.RuntimeState)
                    && !request.RequestedMetricMask.HasFlag(
                        SchedulingProcessMetricMask.MemoryUsage)
                    && request.ExpectedMemoryUsageDependency is null);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
        Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
        Assert.Equal(1, cpuReader.ReleasedSubscriptions);
    }

    [Fact]
    public async Task NormalModeMaintainsPublicResourcesWithoutSchedulingSnapshots()
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false,
            cpuCoreReader: cpuReader);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.PublicResourceManager.TickCalls == 0)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(0, fixture.ProcessFacts.ReadLatestCalls);
            Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.Empty(fixture.ProcessFacts.SubscriptionHistory);
            Assert.Empty(cpuReader.Subscriptions);
            Assert.Equal(1, fixture.MetricSampler.ActiveSubscriptionCount);
            var hardware = Assert.Single(fixture.MetricSampler.SubscriptionHistory);
            Assert.True(hardware.Request.Includes("memory.usage"));
            Assert.False(hardware.Request.Includes("cpu.usage"));
            Assert.False(hardware.Request.Includes("virtualMemory.usage"));
            Assert.False((await fixture.Coordinator.GetStatusAsync(CancellationToken.None))
                .SchedulerRunning);
            var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(
                CancellationToken.None);
            Assert.False(diagnostics.SchedulerRunning);
            Assert.Null(diagnostics.NativeCoordinator);
            Assert.Null(diagnostics.SchedulingAuthority.Cpu);
            Assert.Null(diagnostics.SchedulingAuthority.Gpu);
            Assert.Empty(fixture.Workspace.CurrentSnapshotRows.ToArray());
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
        Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
        Assert.Equal(0, cpuReader.ReleasedSubscriptions);
    }

    [Fact]
    public async Task NormalModeWithoutPublicResourcesHoldsNoSamplingSubscriptions()
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false,
            cpuCoreReader: cpuReader);
        fixture.PublicResourceManager.Available = false;

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.Coordinator.SchedulingAuthority.UnavailableReason
                != "optimization-mode-normal")
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
            Assert.Equal(0, fixture.MetricSampler.ReadLatestCalls);
            Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.Equal(0, fixture.ProcessFacts.ReadLatestCalls);
            Assert.Empty(cpuReader.Subscriptions);
            Assert.Equal(0, fixture.PublicResourceManager.TickCalls);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalModeParksScheduledWakeAndResumesOnPlanOrPublicResourcePublication(
        bool warm)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: warm,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false,
            editFreedomPoints: root => FreedomPointTestFactory.Point(root,
                ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints
                    .BackendFreedomPointPaths.SchedulerSamplingInterval)["value"] = 100);
        fixture.PublicResourceManager.Available = false;
        var deadline = Assert.IsType<HostManagerVersionedWakeDeadline>(
            ReadPrivateField<HostManagerVersionedWakeDeadline>(
                fixture.Coordinator, "schedulingWakeDeadline"));

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.Coordinator.SchedulingAuthority.UnavailableReason
                    != "optimization-mode-normal"
                || deadline.Snapshot.HasDeadline)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.False((await fixture.Coordinator.GetStatusAsync(CancellationToken.None))
                .SchedulerRunning);
            Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);

            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
            {
                Version = 2,
                OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.CpuOnly)
            });
            while (fixture.ProcessFacts.ActiveSubscriptionCount != 1)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.True((await fixture.Coordinator.GetStatusAsync(CancellationToken.None))
                .SchedulerRunning);

            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with { Version = 3 });
            while (fixture.ProcessFacts.ActiveSubscriptionCount != 0
                || deadline.Snapshot.HasDeadline)
            {
                await Task.Delay(10, timeout.Token);
            }
            fixture.PublicResourceManager.Available = true;
            while (fixture.PublicResourceManager.TickCalls == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.False((await fixture.Coordinator.GetStatusAsync(CancellationToken.None))
                .SchedulerRunning);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(AppOptimizationModes.MemoryOnly, true, false, true)]
    [InlineData(AppOptimizationModes.CpuOnly, true, false, false)]
    [InlineData(AppOptimizationModes.GpuOnly, false, true, false)]
    [InlineData(AppOptimizationModes.MemoryCpu, true, false, true)]
    [InlineData(AppOptimizationModes.MemoryGpu, true, true, true)]
    [InlineData(AppOptimizationModes.CpuGpu, true, true, false)]
    public async Task SelectedDomainsOwnOnlyTheirSamplingSubscriptions(
        string mode,
        bool cpu,
        bool gpu,
        bool memory)
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            optimizationMode: mode,
            cpuCoreReader: cpuReader);
        fixture.PublicResourceManager.Available = false;

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.ProcessFacts.ActiveSubscriptionCount != (memory ? 2 : 1))
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(cpu ? 1 : 0, cpuReader.Subscriptions.Count);
            var hardware = Assert.Single(fixture.MetricSampler.SubscriptionHistory).Request;
            Assert.Equal(cpu, hardware.Includes("cpu.usage"));
            Assert.Equal(memory, hardware.Includes("memory.usage"));
            Assert.Equal(memory, hardware.Includes("virtualMemory.usage"));
            Assert.Equal(gpu, hardware.IncludesAllGpuCoreMetrics);
            var process = Assert.Single(fixture.ProcessFacts.SubscriptionHistory,
                static item => item.SubscriptionId == "host-manager-smart-coordinator:compute");
            var expected = SchedulingProcessMetricMask.RuntimeState;
            if (cpu) expected |= SchedulingProcessMetricMask.CpuUsage;
            if (gpu) expected |= SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory;
            Assert.Equal(expected, process.MetricMask);
            Assert.Equal(memory, fixture.ProcessFacts.SubscriptionHistory.Any(
                static item => item.SubscriptionId == "host-manager-smart-coordinator:memory-final-admission"));
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(AppOptimizationModes.MemoryOnly, false, false)]
    [InlineData(AppOptimizationModes.CpuOnly, true, false)]
    [InlineData(AppOptimizationModes.GpuOnly, false, true)]
    [InlineData(AppOptimizationModes.CpuGpu, true, true)]
    [InlineData(AppOptimizationModes.Smart, true, true)]
    public async Task NativeFactsDisableUnselectedAdapterGrades(
        string mode,
        bool cpu,
        bool gpu)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            optimizationMode: mode,
            processFactsSnapshot: CreateCompleteProcessFacts(
                4_241,
                132_537_599_900_000_000,
                "software:domain-test",
                60,
                10,
                20));
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = 2,
            AdapterDispatch = new CompiledAdapterDispatchPlan(
                new Dictionary<string, CompiledAdapterDispatchRoute>(StringComparer.OrdinalIgnoreCase)
                {
                    ["software:domain-test"] = CompiledAdapterDispatchRoute.SoftwareLevelScheduler
                },
                new Dictionary<string, IReadOnlyList<AdapterCpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["software:domain-test"] = [AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize]
                },
                new Dictionary<string, IReadOnlyList<AdapterGpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["software:domain-test"] = [AdapterGpuSchedulingGrade.Normal, AdapterGpuSchedulingGrade.Optimize]
                })
        });

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var identities = fixture.Workspace.InputRows.ToArray()
            .Where(static row => row.MetricKind == NativeSmartCoordinatorMetricKind.None
                && row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessIdentity))
            .ToArray();
        Assert.NotEmpty(identities);
        Assert.All(identities, row =>
        {
            Assert.Equal(cpu, row.CpuCapabilityMask != 0);
            Assert.Equal(gpu, row.GpuCapabilityMask != 0);
        });
    }

    [Fact]
    public async Task BackgroundSchedulerReacquiresComputeSubscriptionsAfterNormalMode()
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false,
            cpuCoreReader: cpuReader,
            editFreedomPoints: root => FreedomPointTestFactory.Point(root,
                ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints
                    .BackendFreedomPointPaths.SchedulerSamplingInterval)["value"] = 100);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.PublicResourceManager.TickCalls == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
            {
                Version = 2,
                OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Smart)
            });
            while (fixture.ProcessFacts.ActiveSubscriptionCount != 2)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(1, fixture.MetricSampler.ActiveSubscriptionCount);
            Assert.Single(cpuReader.Subscriptions);
            Assert.Equal(2, fixture.ProcessFacts.SubscriptionHistory.Count);
            Assert.Equal(2, fixture.MetricSampler.SubscriptionHistory.Count);
            Assert.True(fixture.MetricSampler.SubscriptionHistory[1].Request.Includes("cpu.usage"));

            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with { Version = 3 });
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            while (fixture.ProcessFacts.ActiveSubscriptionCount != 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.Equal(1, cpuReader.ReleasedSubscriptions);
            Assert.Equal(1, fixture.MetricSampler.ActiveSubscriptionCount);
            Assert.False(fixture.MetricSampler.SubscriptionHistory[^1].Request.Includes("cpu.usage"));
            Assert.Null((await fixture.Coordinator.GetDecisionDiagnosticsAsync(
                CancellationToken.None)).NativeCoordinator);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task BackgroundSchedulerUsesTheCompiledFreedomPointForEverySubscription()
    {
        var cpuReader = new RecordingCpuCoreResidencyReader(() => null);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            normalIntervalMilliseconds: 60_000,
            eventIntervalMilliseconds: 60_000,
            editFreedomPoints: root => FreedomPointTestFactory.Point(root,
                ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints
                    .BackendFreedomPointPaths.SchedulerSamplingInterval)["value"] = 2500,
            cpuCoreReader: cpuReader);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.MetricSampler.ReadLatestCalls == 0 || fixture.ProcessFacts.ReadLatestCalls == 0)
                await Task.Delay(10, timeout.Token);

            Assert.Equal(TimeSpan.FromMilliseconds(2500),
                Assert.Single(fixture.MetricSampler.SubscriptionHistory).RefreshInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(2500), Assert.Single(cpuReader.Subscriptions).Interval);
            Assert.Equal(2, fixture.ProcessFacts.SubscriptionHistory.Count);
            Assert.All(fixture.ProcessFacts.SubscriptionHistory,
                subscription => Assert.Equal(TimeSpan.FromMilliseconds(2500), subscription.RefreshInterval));
            Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
            Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
        Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
        Assert.Equal(1, cpuReader.ReleasedSubscriptions);
    }

    [Theory]
    [MemberData(nameof(LifecycleShutdownCases))]
    public async Task ShutdownWaitsForExactWorkerBeforeDisposingOwnedResources(
        int pointValue,
        bool directDispose)
    {
        var point = (HostManagerSmartCoordinatorTransitionPoint)pointValue;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false);
        var probe = new BlockingTransitionProbe(point);
        fixture.Coordinator.TransitionProbe = probe;
        fixture.PublishSmartCoordinatorRecreatePlan();

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        var shutdown = directDispose
            ? Task.Run(fixture.Coordinator.Dispose)
            : fixture.Coordinator.StopAsync(CancellationToken.None);
        await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace"));
        }
        finally
        {
            probe.Release();
        }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            HostManagerSmartCoordinatorLifecycleState.Closed,
            fixture.Coordinator.LifecycleState);
        Assert.True(fixture.Coordinator.ExecuteTask?.IsCompleted ?? true);
        Assert.False(fixture.Coordinator.ExecuteTask?.IsFaulted ?? false);
        Assert.Null(fixture.DeploymentState.Snapshot.SmartCoordinator.ActiveAttempt);
        probe.AssertShutdownOrder();
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task CancelledStopWaitDoesNotCancelSharedShutdown()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false);
        var probe = new BlockingTransitionProbe(
            HostManagerSmartCoordinatorTransitionPoint.AfterReplacementCreate);
        fixture.Coordinator.TransitionProbe = probe;
        fixture.PublishSmartCoordinatorRecreatePlan();
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        using var cancellation = new CancellationTokenSource();
        var cancelledWait = fixture.Coordinator.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = Task.Run(fixture.Coordinator.Dispose);
        try
        {
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Closing,
                fixture.Coordinator.LifecycleState);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace"));
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            probe.Release();
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerSmartCoordinatorLifecycleState.Closed,
            fixture.Coordinator.LifecycleState);
        Assert.False(fixture.Coordinator.ExecuteTask?.IsFaulted ?? false);
        Assert.Null(fixture.DeploymentState.Snapshot.SmartCoordinator.ActiveAttempt);
        probe.AssertShutdownOrder();
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task DisposeWaitsForForegroundCycleHoldingGateAndRejectsNewForegroundWork()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var probe = new BlockingTransitionProbe(
            HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall);
        fixture.Coordinator.TransitionProbe = probe;

        var activeCycle = Task.Run(
            () => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));

        var dispose = Task.Run(fixture.Coordinator.Dispose);
        await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.False(dispose.IsCompleted);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace"));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            Assert.Equal(
                1,
                ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        }
        finally
        {
            probe.Release();
        }
        await activeCycle.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        Assert.Null(fixture.DeploymentState.Snapshot.SmartCoordinator.ActiveAttempt);
        probe.AssertShutdownOrder();
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task ClosedCoordinatorRejectsEveryOperationBeforeStateAccess()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        await fixture.Coordinator.StopAsync(CancellationToken.None);
        var loadCalls = fixture.StateStore.LoadCalls;

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.GetStatusAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.GetStateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.SetModeAsync(
                AppOptimizationModes.Smart,
                CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.RestoreNormalModeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Coordinator.StartAsync(CancellationToken.None));

        Assert.Equal(loadCalls, fixture.StateStore.LoadCalls);
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task StopAndDisposeAreIdempotentBeforeAndAfterStart()
    {
        await using (var cold = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: false))
        {
            cold.Coordinator.Dispose();
            cold.Coordinator.Dispose();
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Closed,
                cold.Coordinator.LifecycleState);
            AssertOwnedResourcesCleared(cold.Coordinator);
        }

        await using var warm = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false);
        await warm.Coordinator.StartAsync(CancellationToken.None);
        await warm.Coordinator.StopAsync(CancellationToken.None);
        await warm.Coordinator.StopAsync(CancellationToken.None);
        warm.Coordinator.Dispose();
        warm.Coordinator.Dispose();

        Assert.Equal(
            HostManagerSmartCoordinatorLifecycleState.Closed,
            warm.Coordinator.LifecycleState);
        AssertOwnedResourcesCleared(warm.Coordinator);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupAdmitsOnlyTheRemainingCycleBudget()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 3);
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static request => new ProcessResourcePolicyBatchWriteResult(
                request.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.DeferredByCycleBudget,
            result.Disposition);
        Assert.Equal(3, result.PlannedCount);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(1, result.TerminalCount);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(result.CompletedNormalReleaseRound);
        Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Single(fixture.ProcessPolicyWriter.BatchRequests);
        Assert.Equal(3, fixture.MemoryCleanupPlanner.LastFeedback.Count);
        Assert.True(fixture.MemoryCleanupPlanner.LastFeedback[0].Attempted);
        Assert.True(fixture.MemoryCleanupPlanner.LastFeedback[0].Succeeded);
        Assert.All(
            fixture.MemoryCleanupPlanner.LastFeedback.Skip(1),
            static feedback =>
            {
                Assert.False(feedback.Attempted);
                Assert.False(feedback.Succeeded);
        });
    }

    [Fact]
    public async Task AutomaticMemoryCleanupValidationHandoffFailureCallsNoWriter()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));
        validation.Committer.FailCommitCallNumber =
            validation.Committer.CommitCallCount + 4;

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BlockedByValidationScope,
            result.Disposition);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Contains(
            "handoff-declare-failed",
            validation.Authority.GetStatus().Failure);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.SchemaVersion,
            evidence.SchemaVersion);
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.Contract,
            evidence.Contract);
        Assert.True(evidence.Sealed);
        Assert.Null(evidence.Failure);
        Assert.Empty(evidence.Batches);
        fixture.AssertNoMemoryCleanupAttemptEntries();
    }

    [Fact]
    public async Task AutomaticMemoryCleanupPlannerGenerationMismatchCallsNoWriter()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        var cleanupGeneration = fixture.RuntimePlan.HostManager.RequirePublished()
            .HotPublish.MemoryCleanup.ConfigurationGeneration;
        fixture.MemoryCleanupPlanner.PlanResult =
            CreateAutomaticMemoryCleanupPlan(request) with
            {
                ConfigurationGeneration = checked(cleanupGeneration + 1)
            };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.PlannerConfigurationMismatch,
            result.Disposition);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.MemoryCleanupPlanner.CompleteCalls);
        var feedback = Assert.Single(fixture.MemoryCleanupPlanner.LastFeedback);
        Assert.False(feedback.Attempted);
        Assert.False(feedback.Succeeded);
        Assert.Empty(evidence.Batches);
        Assert.True(evidence.Sealed);
        fixture.AssertNoMemoryCleanupAttemptEntries();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticMemoryCleanupEmptyPlannerGenerationMismatchCallsNoWriter(
        bool zeroGeneration)
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        var cleanupGeneration = fixture.RuntimePlan.HostManager.RequirePublished()
            .HotPublish.MemoryCleanup.ConfigurationGeneration;
        fixture.MemoryCleanupPlanner.PreserveZeroConfigurationGeneration = zeroGeneration;
        fixture.MemoryCleanupPlanner.PlanResult = new(
            [],
            StateRevision: 37,
            ConfigurationGeneration: zeroGeneration
                ? 0
                : checked(cleanupGeneration + 1));
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        var validationCommitCount = validation.Committer.CommitCallCount;
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.PlannerConfigurationMismatch,
            result.Disposition);
        Assert.False(result.CompletedNormalReleaseRound);
        Assert.Equal(0, result.CompletedAtUtcTicks);
        Assert.Equal(37UL, result.PlannerStateRevision);
        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.MemoryCleanupPlanner.CompleteCalls);
        Assert.Equal(validationCommitCount, validation.Committer.CommitCallCount);
        Assert.Empty(evidence.Batches);
        Assert.True(evidence.Sealed);
        fixture.AssertNoMemoryCleanupAttemptEntries();
    }

    [Fact]
    public async Task AutomaticMemoryCleanupScopedExecutionPublishesCausalEvidence()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull,
            result.Disposition);
        Assert.Equal(validation.ScopeId, evidence.ScopeId);
        Assert.Equal(validation.Authority.Capture().Generation, evidence.ScopeGeneration);
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.SchemaVersion,
            evidence.SchemaVersion);
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.Contract,
            evidence.Contract);
        Assert.True(evidence.Sealed);
        Assert.Null(evidence.Failure);
        var batch = Assert.Single(evidence.Batches);
        var cleanupPlan = fixture.RuntimePlan.HostManager.RequirePublished()
            .HotPublish.MemoryCleanup;
        Assert.Equal(1UL, batch.CycleSequence);
        Assert.Equal(1UL, batch.AttemptGeneration);
        Assert.Equal(cleanupPlan.ConfigurationGeneration,
            batch.PlannerConfigurationGeneration);
        Assert.Equal(cleanupPlan.GuardedFreeRatio, batch.GuardedFreeRatio);
        Assert.Equal(cleanupPlan.PhysicalEmergencyFreeRatio,
            batch.PhysicalEmergencyFreeRatio);
        Assert.Equal(cleanupPlan.VirtualEmergencyFreeRatio,
            batch.VirtualEmergencyFreeRatio);
        Assert.Equal(1UL, batch.CapacityWorkspaceIdentity);
        Assert.Equal(1UL, batch.CapacityConfigurationGeneration);
        Assert.Equal(1UL, batch.CapacityCatalogGeneration);
        Assert.Equal(1UL, batch.BaselineCapacityCommittedGeneration);
        Assert.Equal(2UL, batch.FinalCapacityCommittedGeneration);
        Assert.Equal(1L, batch.BaselineCapacityCapturedAtUtcTicks);
        Assert.Equal(2L, batch.FinalCapacityCapturedAtUtcTicks);
        Assert.Equal(request.Kind, batch.RequestKind);
        Assert.Equal(request.OrdinaryMemoryFreeRatio, batch.OrdinaryMemoryFreeRatio);
        Assert.True(batch.OrdinaryMemoryFreeRatioCurrent);
        Assert.Equal(request.PhysicalMemoryFreeRatio, batch.PhysicalMemoryFreeRatio);
        Assert.True(batch.PhysicalMemoryFreeRatioCurrent);
        Assert.Equal(request.VirtualMemoryFreeRatio, batch.VirtualMemoryFreeRatio);
        Assert.True(batch.VirtualMemoryFreeRatioCurrent);
        Assert.Equal(AutomaticMemoryCleanupMode.Normal, batch.SelectedMode);
        Assert.Equal(request.Candidates.Count, batch.CandidateCount);
        Assert.Equal(1, batch.RequestedCount);
        Assert.True(batch.ResultShapeExact);
        Assert.True(batch.OutcomeKnown);
        Assert.True(batch.JournalSettled);
        Assert.True(batch.WriterStartedAt.UtcTicks > 0);
        Assert.True(batch.WriterStartedAtQpcTicks > 0);
        Assert.True(batch.CompletedAt >= batch.WriterStartedAt);
        Assert.True(batch.CompletedAtQpcTicks >= batch.WriterStartedAtQpcTicks);
        Assert.True(batch.CompletedAtQpcTicks > 0);
        var action = Assert.Single(batch.Actions);
        Assert.Equal(0, action.ActionIndex);
        Assert.Equal(0, action.SourceInputIndex);
        Assert.Equal(candidate.ProcessId, action.ProcessId);
        Assert.Equal(candidate.ProcessStartedAt.ToFileTime(),
            action.ProcessStartTimeFileTimeUtc);
        Assert.True(action.ResultReturned);
        Assert.True(action.Succeeded);
        Assert.Equal(0U, action.Win32Error);

        var secondAdmission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        secondAdmission.InitializeBudgets(1, 1);
        Assert.True(secondAdmission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var secondPermit));

        var blockedAfterSeal = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            secondPermit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.ValidationEvidenceCapacityUnavailable,
            blockedAfterSeal.Disposition);
        Assert.False(blockedAfterSeal.HasUnresolvedCurrentEffects);
        Assert.Equal(1U, secondAdmission.NewPointOfNoReturnRemaining);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupScopedExecutionExcludesEveryOutsideScopeCandidate()
    {
        var request = CreateAutomaticMemoryCleanupRequest(
            HostManagerProcessEffectValidationScopeAuthority.MaximumAllowedProcessCount + 1);
        request = request with
        {
            Candidates = request.Candidates
                .Select((candidate, index) => candidate with { CanApply = index == 0 })
                .ToArray()
        };
        var allowedCandidate = request.Candidates[0];
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            allowedCandidate.ProcessId,
            checked((ulong)allowedCandidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        fixture.MemoryCleanupPlanner.PlanHandler = CreateAutomaticMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull,
            result.Disposition);
        Assert.Equal(request.Candidates.Count, result.CandidateCount);
        Assert.Equal(1, result.PlannedCount);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(
            allowedCandidate,
            Assert.Single(fixture.MemoryCleanupPlanner.LastRequest!.Candidates));
        Assert.Equal(
            allowedCandidate.ProcessId,
            Assert.Single(fixture.ProcessPolicyWriter.BatchRequests).ProcessId);
        var batch = Assert.Single(evidence.Batches);
        Assert.Equal(1, batch.CandidateCount);
        Assert.Equal(1, batch.RequestedCount);
        Assert.Equal(allowedCandidate.ProcessId, Assert.Single(batch.Actions).ProcessId);
    }

    [Theory]
    [InlineData("extra-field")]
    [InlineData("duplicate-trim")]
    [InlineData("success-nonzero-error")]
    public async Task AutomaticMemoryCleanupScopedMalformedWriterResultRemainsUnknown(
        string malformedShape)
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        candidate.ProcessStartedAt)),
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests
            .Select(item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                malformedShape switch
                {
                    "extra-field" =>
                    [
                        new(
                            ProcessResourcePolicyBatchFields.TrimWorkingSet,
                            Succeeded: true,
                            "trimmed"),
                        new(
                            ProcessResourcePolicyBatchFields.MemoryPriority,
                            Succeeded: true,
                            "unexpected")
                    ],
                    "duplicate-trim" =>
                    [
                        new(
                            ProcessResourcePolicyBatchFields.TrimWorkingSet,
                            Succeeded: true,
                            "trimmed"),
                        new(
                            ProcessResourcePolicyBatchFields.TrimWorkingSet,
                            Succeeded: true,
                            "duplicate")
                    ],
                    "success-nonzero-error" =>
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.TrimWorkingSet,
                        Succeeded: true,
                        "contradictory")
                    {
                        ErrorCode = 5
                    }],
                    _ => throw new InvalidOperationException("Unknown malformed shape.")
                }))
            .ToArray();
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
            result.Disposition);
        Assert.Equal(0, result.TerminalCount);
        Assert.Equal(0, result.SucceededCount);
        var batch = Assert.Single(evidence.Batches);
        Assert.False(batch.ResultShapeExact);
        Assert.False(batch.OutcomeKnown);
        var action = Assert.Single(batch.Actions);
        Assert.False(action.ResultReturned);
        Assert.False(action.Succeeded);
        Assert.Null(action.Win32Error);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupScopedWriterFailurePublishesUnknownAfterDurableBlock()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime())));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        candidate.ProcessStartedAt)),
            processEffectValidationScopeAuthority: validation.Authority);
        InitializeMemoryCleanupValidationEvidence(
            fixture.Coordinator,
            validation.Authority.Capture());
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new IOException("injected scoped unknown trim outcome");
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);
        var evidence = await fixture.Coordinator
            .GetMemoryCleanupValidationEvidenceAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
            result.Disposition);
        Assert.True(evidence.Sealed);
        Assert.Null(evidence.Failure);
        var batch = Assert.Single(evidence.Batches);
        Assert.False(batch.ResultShapeExact);
        Assert.False(batch.OutcomeKnown);
        Assert.False(batch.JournalSettled);
        Assert.True(batch.WriterStartedAt.UtcTicks > 0);
        Assert.True(batch.WriterStartedAtQpcTicks > 0);
        Assert.True(batch.CompletedAt >= batch.WriterStartedAt);
        Assert.True(batch.CompletedAtQpcTicks >= batch.WriterStartedAtQpcTicks);
        var action = Assert.Single(batch.Actions);
        Assert.Equal(candidate.ProcessId, action.ProcessId);
        Assert.False(action.ResultReturned);
        Assert.False(action.Succeeded);
        Assert.Null(action.Win32Error);

        fixture.CloseMemoryCleanupJournalForRestart();
        using var restartedJournal = new HostManagerMemoryCleanupAttemptJournal(
            fixture.MemoryCleanupJournalPath,
            () => fixture.RuntimePlan.HostManager.HostRecreate.MemoryCleanup.StateCapacity,
            TimeProvider.System);
        var blocked = restartedJournal.ReconcileAndCaptureBlocked(
            fixture.ProcessPolicyWriter.ReadProcessInstanceForRecovery);
        Assert.Contains(
            new HostManagerComputeProcessIdentity(
                candidate.ProcessId,
                checked((ulong)candidate.ProcessStartedAt.ToFileTime())),
            blocked);
    }

    [Fact]
    public async Task CloseValidationScopeRetiresCommittedPreparedBeforeWriterBatch()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var decision = Assert.Single(CreateAutomaticMemoryCleanupPlan(request).Decisions);
        var candidate = decision.Candidate;
        var identity = new HostManagerComputeProcessIdentity(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime()));
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processRecoveryRead: static _ => throw new InvalidOperationException(
                "Prepared-before-writer retirement must not inspect the process."),
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        const ulong attemptGeneration = 41;
        var batch = new HostManagerMemoryCleanupAttemptBatch(
            Guid.NewGuid(),
            new HashSet<HostManagerComputeProcessIdentity> { identity },
            attemptGeneration);
        Assert.True(validation.Authority.TryBeginBatchAdmission(
            validation.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "memory-cleanup-pre-writer",
            [identity],
            out var permit));
        Assert.NotNull(permit);
        Assert.True(validation.Authority.TryDeclareMemoryCleanupAttemptBatch(
            permit,
            batch,
            attemptGeneration));
        _ = fixture.MemoryCleanupJournal.PrepareBeforeWriter(
            batch.BatchId,
            attemptGeneration,
            [decision]);
        Assert.True(validation.Authority.TryCommitMemoryCleanupAttemptBatch(
            permit,
            batch,
            attemptGeneration));
        Assert.Single(fixture.MemoryCleanupJournal.CapturePendingBatches());

        var closed = await fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
            validation.CreateCloseRequest(),
            CancellationToken.None);

        Assert.Equal("closed", closed.State);
        Assert.Empty(fixture.MemoryCleanupJournal.CapturePendingBatches());
        Assert.True(validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Theory]
    [InlineData((int)NativeTransactionJournalScope.Software, false)]
    [InlineData((int)NativeTransactionJournalScope.Resource, true)]
    public async Task CloseValidationScopeRejectsAnyPendingJournalRecord(
        int scopeValue,
        bool recoveryRequired)
    {
        var scope = (NativeTransactionJournalScope)scopeValue;
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            ProcessId: 25_001,
            ProcessStartKey: 132_537_600_100_000_000));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        await fixture.PreparePendingTransactionAsync(scope);
        if (recoveryRequired)
        {
            fixture.SimulateRecoveryRequiredJournalForValidationRead();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                validation.CreateCloseRequest(),
                CancellationToken.None));

        Assert.Contains("empty transaction journal", exception.Message);
        var journal = await fixture.ReadJournalSnapshotAsync();
        Assert.Equal((uint)scope, Assert.Single(journal.Records).Scope);
        Assert.False(validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task CloseValidationScopeStillRejectsPendingProcessTransaction()
    {
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            ProcessId: 25_002,
            ProcessStartKey: 132_537_600_200_000_000));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        await fixture.PreparePendingTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                validation.CreateCloseRequest(),
                CancellationToken.None));

        Assert.Contains("empty transaction journal", exception.Message);
        Assert.False(validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task CloseValidationScopeRejectsAdapterOwnership()
    {
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            ProcessId: 25_003,
            ProcessStartKey: 132_537_600_300_000_000));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        await fixture.SeedAdapterOwnershipAsync("software:validation-unrelated", 501);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                validation.CreateCloseRequest(),
                CancellationToken.None));

        Assert.Contains("no applied ownership", exception.Message);
        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Equal(
            (uint)NativeAppliedOwnershipScope.Adapter,
            Assert.Single(ownership.Records).Primary.Scope);
        Assert.False(validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task CloseValidationScopeStillRejectsAppliedProcessOwnership()
    {
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            ProcessId: 25_004,
            ProcessStartKey: 132_537_600_400_000_000));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        _ = await fixture.SeedProcessOwnershipAsync(
            processId: 25_005,
            processStartKey: 132_537_600_500_000_000,
            softwareId: "software:validation-process",
            memoryPolicy: false,
            actionId: 502);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                validation.CreateCloseRequest(),
                CancellationToken.None));

        Assert.Contains("no applied ownership", exception.Message);
        Assert.False(validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupSuccessfulEmptyPlanCompletesKnownRound()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        fixture.MemoryCleanupPlanner.PlanResult = AutomaticMemoryCleanupPlanResult.Empty;
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.CompleteNoDecision,
            result.Disposition);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0UL, result.PlannerStateRevision);
        Assert.True(result.CompletedNormalReleaseRound);
        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.MemoryCleanupPlanner.CompleteCalls);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupPrepareFailureReturnsPrePonrBudget()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            memoryCleanupStateCapacity: 1);
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 2);
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(2, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.JournalPrepareFailed,
            result.Disposition);
        Assert.Equal(2, result.PlannedCount);
        Assert.Equal(0, result.RequestedCount);
        Assert.Equal(0, result.TerminalCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.False(result.CompletedNormalReleaseRound);
        Assert.Equal(2U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(2, fixture.MemoryCleanupPlanner.LastFeedback.Count);
        Assert.All(
            fixture.MemoryCleanupPlanner.LastFeedback,
            static feedback =>
            {
                Assert.False(feedback.Attempted);
                Assert.False(feedback.Succeeded);
            });
    }

    [Fact]
    public async Task AutomaticMemoryCleanupCancellationBeforeReservationKeepsBudget()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeExecuteMemoryCleanup(
                fixture.Coordinator,
                permit,
                request,
                cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        var feedback = Assert.Single(fixture.MemoryCleanupPlanner.LastFeedback);
        Assert.False(feedback.Attempted);
        Assert.False(feedback.Succeeded);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupCancellationAfterReservationConsumesBudget()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        candidate.ProcessStartedAt)));
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new OperationCanceledException("external batch outcome is unknown");
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
            result.Disposition);
        Assert.Equal(1, result.PlannedCount);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(0, result.TerminalCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.False(result.CompletedNormalReleaseRound);
        Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        var feedback = Assert.Single(fixture.MemoryCleanupPlanner.LastFeedback);
        Assert.True(feedback.Attempted);
        Assert.False(feedback.Succeeded);

        fixture.CloseMemoryCleanupJournalForRestart();
        using var restartedJournal = new HostManagerMemoryCleanupAttemptJournal(
            fixture.MemoryCleanupJournalPath,
            () => fixture.RuntimePlan.HostManager.HostRecreate.MemoryCleanup.StateCapacity,
            TimeProvider.System);
        var blocked = restartedJournal.ReconcileAndCaptureBlocked(
            fixture.ProcessPolicyWriter.ReadProcessInstanceForRecovery);
        Assert.Contains(
            new HostManagerComputeProcessIdentity(
                candidate.ProcessId,
                checked((ulong)candidate.ProcessStartedAt.ToFileTime())),
            blocked);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupPriorUnknownCannotBecomeACompletedEmptyRound()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var candidate = Assert.Single(request.Candidates);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        candidate.ProcessStartedAt)));
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new IOException("injected unknown trim outcome");
        var firstAdmission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        firstAdmission.InitializeBudgets(1, 1);
        Assert.True(firstAdmission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var firstPermit));

        var first = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            firstPermit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
            first.Disposition);
        Assert.False(first.CompletedNormalReleaseRound);

        fixture.MemoryCleanupPlanner.PlanResult = AutomaticMemoryCleanupPlanResult.Empty;
        var secondAdmission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        secondAdmission.InitializeBudgets(1, 1);
        Assert.True(secondAdmission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var secondPermit));

        var second = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            secondPermit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BlockedByPriorUnknownOutcome,
            second.Disposition);
        Assert.Equal(1, second.CandidateCount);
        Assert.Equal(1, second.BlockedCandidateCount);
        Assert.False(second.CompletedNormalReleaseRound);
        Assert.Empty(Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest).Candidates);
        Assert.Equal(1U, secondAdmission.NewPointOfNoReturnRemaining);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task AutomaticMemoryCleanupPriorUnknownDoesNotBlockOtherCandidates()
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 2);
        var blockedCandidate = request.Candidates[0];
        var executableCandidate = request.Candidates[1];
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false,
            processRecoveryRead: processId =>
            {
                var candidate = Assert.Single(request.Candidates, item =>
                    item.ProcessId == processId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        candidate.ProcessStartedAt));
            });
        fixture.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(
            request with { Candidates = [blockedCandidate] });
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new IOException("injected unknown trim outcome");
        var firstAdmission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        firstAdmission.InitializeBudgets(1, 1);
        Assert.True(firstAdmission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var firstPermit));

        var first = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            firstPermit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
            first.Disposition);
        fixture.MemoryCleanupPlanner.PlanHandler = CreateAutomaticMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = static requests => requests
            .Select(static item => new ProcessResourcePolicyBatchWriteResult(
                item.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet,
                    Succeeded: true,
                    "trimmed")]))
            .ToArray();
        var secondAdmission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        secondAdmission.InitializeBudgets(1, 1);
        Assert.True(secondAdmission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var secondPermit));

        var second = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            secondPermit,
            request,
            CancellationToken.None);

        Assert.Equal(
            HostManagerMemoryCleanupRoundDisposition.BlockedByPriorUnknownOutcome,
            second.Disposition);
        Assert.Equal(2, second.CandidateCount);
        Assert.Equal(1, second.BlockedCandidateCount);
        Assert.Equal(1, second.PlannedCount);
        Assert.Equal(1, second.RequestedCount);
        Assert.Equal(1, second.TerminalCount);
        Assert.Equal(1, second.SucceededCount);
        Assert.False(second.CompletedNormalReleaseRound);
        Assert.Equal(
            executableCandidate.ProcessId,
            Assert.Single(fixture.ProcessPolicyWriter.BatchRequests).ProcessId);
        Assert.Equal(0U, secondAdmission.NewPointOfNoReturnRemaining);
        Assert.Equal(2, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task RealtimeFinalAdmissionSharesOneNewPonrWithoutSwitchingScoreOnly()
    {
        const int processId = 24_001;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        processStartedAt));
            },
            memoryModePolicyEnabled: true,
            cpuUsagePercent: 1);
        await fixture.PrimeMemoryCleanupAdmissionAsync();
        fixture.MetricSampler.SetCpuUsagePercent(99);
        fixture.PublishNextHostedCpuObservation();
        var currentMemoryPriority = 5U;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "cycle-budget",
                @"c:\tests\cycle-budget.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            var targetMemoryPriority = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            currentMemoryPriority = targetMemoryPriority;
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };
        fixture.MemoryCleanupPlanner.PlanHandler = request =>
            CreateAutomaticMemoryCleanupPlan(request);
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        var publicTickCallsBeforeFinalCycle = fixture.PublicResourceManager.TickCalls;
        var nativePlanProbe = new CapturingNativePlanProbe(fixture.Workspace);
        fixture.Coordinator.TransitionProbe = nativePlanProbe;

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty(fixture.ProcessPolicyWriter.BatchRequests);
        Assert.Equal(5U, currentMemoryPriority);
        Assert.Equal(1, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Single(fixture.MemoryCleanupPlanner.LastRequest!.Candidates);
        var trimFeedback = Assert.Single(fixture.MemoryCleanupPlanner.LastFeedback);
        Assert.False(trimFeedback.Attempted);
        Assert.False(trimFeedback.Succeeded);
        Assert.Equal(
            publicTickCallsBeforeFinalCycle + 1,
            fixture.PublicResourceManager.TickCalls);
        Assert.Equal(
            1U,
            fixture.PublicResourceManager.LastRequest!.Value.MaximumNewEffectAttempts);
        Assert.Equal(1U, fixture.PublicResourceManager.NewEffectAttemptCount);
        Assert.Equal(0, fixture.PublicResourceManager.PendingNewEffectCandidates);
        Assert.False(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.False(nativePlanProbe.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.DoesNotContain(
            nativePlanProbe.Actions,
            static action => action.Flags.HasFlag(
                NativeSmartCoordinatorActionFlags.RequiresFeedback));
        Assert.True(
            nativePlanProbe.Actions.All(
                static action => action.Disposition ==
                    NativeSmartCoordinatorActionDisposition.NoOp),
            string.Join(
                Environment.NewLine,
                nativePlanProbe.Actions.Select(static action =>
                    $"scope={action.Scope}; disposition={action.Disposition}; " +
                    $"process={action.FromProcessGrade}->{action.ToProcessGrade}; " +
                    $"cpu={action.FromCpuGrade}->{action.ToCpuGrade}; " +
                    $"gpu={action.FromGpuGrade}->{action.ToGpuGrade}; " +
                    $"flags={action.Flags}")));
    }

    [Fact]
    public async Task UnknownCleanupStopsCurrentCycleWithoutStarvingOtherWork()
    {
        const int processId = 24_220;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, cpuUsagePercent: 1,
            processFactsSnapshot: CreateCompleteProcessFacts(processId, startKey,
                "software:cleanup-unknown", 30, 0, 25),
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(pid, startedAt)));
        await fixture.PrimeMemoryCleanupAdmissionAsync();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            fixture.MetricSampler.PublishNextHostedMemoryObservation();
            fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                fixture.ProcessFacts.Snapshot, fixture.MetricSampler.Snapshot));
            _ = await fixture.RunRealtimeCycleAsync();
        }
        fixture.MemoryCleanupPlanner.PlanHandler = CreateAutomaticMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            Assert.True(Assert.Single(requests).TrimWorkingSet);
            throw new InvalidOperationException("simulated unknown trim");
        };
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        var publicTicks = fixture.PublicResourceManager.TickCalls;
        var nativeSequence = fixture.Workspace.Snapshot.CycleSequence;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            fixture.MetricSampler.PublishNextHostedMemoryObservation();
            fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                fixture.ProcessFacts.Snapshot, fixture.MetricSampler.Snapshot));
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(publicTicks + attempt, fixture.PublicResourceManager.TickCalls);
            Assert.Equal(attempt == 0 ? nativeSequence : nativeSequence + 2,
                fixture.Workspace.Snapshot.CycleSequence);
            Assert.Equal((uint)attempt, fixture.PublicResourceManager.NewEffectAttemptCount);
            Assert.Equal(attempt == 0 ? 3 : 0, typeof(HostManagerSmartCoordinator).GetField(
                "nextRealtimeNewEffectIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Coordinator));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealtimeNewEffectsProgressWhileMemoryBacklogRemains(bool failOwnedRestore)
    {
        const int firstProcessId = 24_200;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var inputs = Enumerable.Range(firstProcessId, 12)
            .Select(pid => (pid, startKey, $"software:fair-budget:{pid}", 40d, 1d, 1d))
            .ToArray();
        var states = inputs.ToDictionary(input => input.pid, input =>
            new ProcessResourcePolicySnapshot(input.pid, $"budget-{input.pid}",
                $@"c:\tests\budget-{input.pid}.exe", startedAt, "Normal", 1, 1,
                MemoryPriority: 5, PowerThrottlingControlMask: 0, PowerThrottlingStateMask: 0));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(inputs),
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(pid, startedAt)),
            memoryModePolicyEnabled: true, cpuUsagePercent: 99);
        var memoryWrites = 0;
        var memoryRestoreWrites = 0;
        var nativeWrites = 0;
        var nativeRestoreWrites = 0;
        var trimWrites = 0;
        fixture.ProcessPolicyWriter.ProcessReadHandler = pid => states[pid];
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            var current = states[request.ProcessId];
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            if (request.MemoryPriority is { } memoryPriority)
            {
                memoryWrites++;
                if (memoryPriority == 5)
                {
                    memoryRestoreWrites++;
                }
                current = current with { MemoryPriority = memoryPriority };
                fields.Add(new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "applied"));
            }
            if (request.PriorityClass is { } priorityClass)
            {
                nativeWrites++;
                if (priorityClass == "Normal")
                {
                    nativeRestoreWrites++;
                    if (failOwnedRestore)
                    {
                        throw new InvalidOperationException("simulated unknown owned restore");
                    }
                }
                current = current with { PriorityClass = priorityClass };
                fields.Add(new(ProcessResourcePolicyBatchFields.PriorityClass, true, "applied"));
            }
            if (request.PowerControlMask is { } powerControl)
            {
                current = current with
                {
                    PowerThrottlingControlMask = powerControl,
                    PowerThrottlingStateMask = request.PowerStateMask
                };
                fields.Add(new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "applied"));
            }
            if (request.TrimWorkingSet)
            {
                trimWrites++;
                fields.Add(new(ProcessResourcePolicyBatchFields.TrimWorkingSet, true, "applied"));
            }
            states[request.ProcessId] = current;
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId, fields);
        }).ToArray();
        fixture.MemoryCleanupPlanner.PlanHandler = CreateAutomaticMemoryCleanupPlan;
        fixture.PublicResourceManager.PendingNewEffectCandidates = 20;
        var nativePlanProbe = new CapturingNativePlanProbe(fixture.Workspace);
        fixture.Coordinator.TransitionProbe = nativePlanProbe;
        var history = new List<string>();

        void RecordCycle(int cycle)
        {
            history.Add($"cycle={cycle}; memory={memoryWrites}; native={nativeWrites}; trim={trimWrites}; "
                + $"public={fixture.PublicResourceManager.NewEffectAttemptCount}; actions="
                + string.Join(",", nativePlanProbe.Actions.Select(action =>
                    $"{action.ProcessId}:{action.Disposition}:{action.FromProcessGrade}->{action.ToProcessGrade}"))
                + "; rows=" + string.Join(",", fixture.Workspace.CurrentSnapshotRows.ToArray()
                    .Where(row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process)
                    .Select(row => $"{row.ProcessId}:{row.DesiredProcessGrade}/{row.AppliedProcessGrade}"
                        + $"/{row.PendingProcessGrade}/{row.ProcessPendingCount}")));
        }

        for (var cycle = 0; cycle < 8; cycle++)
        {
            fixture.PublishNextHostedCpuObservation();
            fixture.MetricSampler.PublishNextHostedMemoryObservation();
            fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                fixture.ProcessFacts.Snapshot, fixture.MetricSampler.Snapshot));
            var before = memoryWrites + nativeWrites + trimWrites
                + fixture.PublicResourceManager.NewEffectAttemptCount;
            _ = await fixture.RunRealtimeCycleAsync();
            var after = memoryWrites + nativeWrites + trimWrites
                + fixture.PublicResourceManager.NewEffectAttemptCount;
            RecordCycle(cycle);
            Assert.InRange(after - before, 0, 1);
            Assert.Equal(0u, fixture.Workspace.Snapshot.InflightCount);
        }

        Assert.InRange(memoryWrites, 1, 11);
        Assert.True(nativeWrites > 0, "Native effects never ran while memory work remained.");
        Assert.True(trimWrites > 0, "Working-set cleanup never received the shared budget.");
        Assert.True(fixture.PublicResourceManager.NewEffectAttemptCount > 0);
        Assert.False(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.Contains(fixture.Workspace.CurrentSnapshotRows.ToArray(), row =>
            row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
            && row.AppliedProcessGrade != NativeSmartCoordinatorProcessGrade.Normal);
        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.NotEmpty(ownership.Records);

        fixture.ProcessFacts.SetSnapshot(fixture.ProcessFacts.Snapshot with
        {
            Processes = fixture.ProcessFacts.Snapshot.Processes
                .Select(static process => process with { BaseScore = 90 })
                .ToArray()
        });
        var restoredAlongsideNewEffect = false;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            fixture.PublishNextHostedCpuObservation();
            fixture.MetricSampler.PublishNextHostedMemoryObservation();
            fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                fixture.ProcessFacts.Snapshot, fixture.MetricSampler.Snapshot));
            var newBefore = memoryWrites - memoryRestoreWrites
                + nativeWrites - nativeRestoreWrites + trimWrites
                + fixture.PublicResourceManager.NewEffectAttemptCount;
            var memoryRecoveryBefore = memoryRestoreWrites;
            var recoveryBefore = nativeRestoreWrites;
            var publicTicksBefore = fixture.PublicResourceManager.TickCalls;
            var memoryWritesBefore = memoryWrites;
            var trimWritesBefore = trimWrites;
            var newEffectIndexBefore = (int)typeof(HostManagerSmartCoordinator).GetField(
                "nextRealtimeNewEffectIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Coordinator)!;
            _ = await fixture.RunRealtimeCycleAsync();
            RecordCycle(cycle + 8);
            if (failOwnedRestore && nativeRestoreWrites != recoveryBefore)
            {
                Assert.True(fixture.Workspace.Snapshot.Flags.HasFlag(
                    NativeSmartCoordinatorSnapshotFlags.RequiresAuthoritativeResync));
                Assert.Equal(publicTicksBefore, fixture.PublicResourceManager.TickCalls);
                Assert.Equal(memoryWritesBefore, memoryWrites);
                Assert.Equal(trimWritesBefore, trimWrites);
                Assert.Equal(newEffectIndexBefore, typeof(HostManagerSmartCoordinator).GetField(
                    "nextRealtimeNewEffectIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(fixture.Coordinator));
                return;
            }
            var newAfter = memoryWrites - memoryRestoreWrites
                + nativeWrites - nativeRestoreWrites + trimWrites
                + fixture.PublicResourceManager.NewEffectAttemptCount;
            var newEffectDelta = newAfter - newBefore;
            var recoveryDelta = memoryRestoreWrites - memoryRecoveryBefore
                + nativeRestoreWrites - recoveryBefore;
            Assert.True(
                newEffectDelta is >= 0 and <= 1,
                $"newEffectDelta={newEffectDelta}; recoveryDelta={recoveryDelta}"
                    + Environment.NewLine + string.Join(Environment.NewLine, history));
            Assert.InRange(recoveryDelta, 0, 1);
            restoredAlongsideNewEffect |= newAfter > newBefore && recoveryDelta > 0;
            Assert.Equal(0u, fixture.Workspace.Snapshot.InflightCount);
        }
        Assert.True(fixture.Workspace.CurrentSnapshotRows.ToArray()
            .Where(row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process)
            .All(row => row.AppliedProcessGrade == NativeSmartCoordinatorProcessGrade.Normal),
            string.Join(Environment.NewLine, history));
        Assert.All(fixture.Workspace.CurrentSnapshotRows.ToArray()
            .Where(row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process),
            row =>
            {
                Assert.Equal(NativeSmartCoordinatorProcessGrade.Normal, row.DesiredProcessGrade);
                Assert.Equal(NativeSmartCoordinatorProcessGrade.Normal, row.AppliedProcessGrade);
            });
        Assert.All(states.Values, state => Assert.Equal("Normal", state.PriorityClass));
        Assert.True(restoredAlongsideNewEffect, string.Join(Environment.NewLine, history));
        Assert.False(failOwnedRestore, "The intended owned-restore failure was never exercised.");
    }

    [Fact]
    public async Task ExplicitLowTierPagedFrozenIsClosedBeforeMemoryWriter()
    {
        const int processId = 24_045;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                "software:explicit-low-paged-frozen",
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            policyExecutionEnabled: true,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            cpuUsagePercent: 1);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 100,
            cpuUsagePercent: 1));
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("PagedFrozen must remain closed before the writer.");

        _ = await fixture.RunRealtimeCycleAsync();

        var projection = Assert.IsType<HostManagerNonAdaptedMemoryModeProjectionSnapshot>(
            fixture.Coordinator.NonAdaptedMemoryModeProjection);
        var directive = Assert.Single(projection.Processes);
        Assert.Equal(NativeMemoryMode.PagedFrozen, directive.Mode);
        Assert.Equal(
            HostManagerNonAdaptedMemoryProcessAction.PagedFrozen,
            directive.Action);
        Assert.Equal(1U, directive.TargetMemoryPriority);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty(fixture.ProcessPolicyWriter.BatchRequests);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Fact]
    public async Task ExplicitLowTierLevel4IsRejectedBeforeProcessWriter()
    {
        const int processId = 24_046;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        processFacts = processFacts with
        {
            RequestedMetricMask = processFacts.RequestedMetricMask
                | SchedulingProcessMetricMask.GpuUsage,
            CurrentMetricMask = processFacts.CurrentMetricMask
                | SchedulingProcessMetricMask.GpuUsage,
            GpuSourceGeneration = 1,
            GpuObservedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            GpuTopologyGeneration = 1,
            GpuTopologyFingerprint = 0,
            Processes =
            [
                processFacts.Processes[0] with
                {
                    SoftwareKind = SoftwareKinds.Other,
                    SoftwareDisplayKind = SoftwareKinds.Other,
                    ValidMetricMask = processFacts.Processes[0].ValidMetricMask
                        | SchedulingProcessMetricMask.GpuUsage
                }
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: true,
            memoryModePolicyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 99,
            optimizationCapabilities:
                OptimizationModeCapabilities.AutomaticProcessPolicies);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 10,
            cpuUsagePercent: 99,
            gpuInventoryCurrent: true));
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "explicit-low-level4",
                @"c:\tests\explicit-low-level4.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5);
        };
        var nativePlanProbe = new CapturingNativePlanProbe(fixture.Workspace);
        fixture.Coordinator.TransitionProbe = nativePlanProbe;
        NativeSmartCoordinatorAction? level4Action = null;

        for (var cycle = 0; cycle < 8 && level4Action is null; cycle++)
        {
            if (cycle != 0)
            {
                fixture.PublishNextHostedCpuObservation();
            }
            _ = await fixture.RunRealtimeCycleAsync();
            level4Action = nativePlanProbe.Actions.SingleOrDefault(static action =>
                action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                    && action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
                    && action.ToProcessGrade == NativeSmartCoordinatorProcessGrade.Level4);
            if (level4Action.Value.ActionId == 0)
            {
                level4Action = null;
            }
        }

        var identity = Assert.Single(
            fixture.Workspace.InputRows.ToArray(),
            row => row.StructSize != 0
                && row.MetricKind == NativeSmartCoordinatorMetricKind.None
                && row.ProcessId == processId);
        var processAfterPlanning = Assert.Single(
            fixture.Workspace.CurrentSnapshotRows.ToArray(),
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
                && row.ProcessId == processId);
        Assert.True(
            level4Action.HasValue,
            $"No L4 action was planned. inputFlags={identity.Flags}; " +
            $"kind={identity.SoftwareKind}; base={identity.BaseScore}; " +
            $"desired={processAfterPlanning.DesiredProcessGrade}; " +
            $"pending={processAfterPlanning.PendingProcessGrade}; " +
            $"pendingCount={processAfterPlanning.ProcessPendingCount}; " +
            $"cpuScore={processAfterPlanning.CpuScore}; " +
            $"occupancy={processAfterPlanning.CpuOccupancyPercent}; " +
            $"reason={processAfterPlanning.ReasonMask}; " +
            $"snapshotReason={fixture.Workspace.Snapshot.ReasonMask}; " +
            $"actions={string.Join(", ", nativePlanProbe.Actions.Select(static action =>
                $"{action.Scope}:{action.Disposition}:" +
                $"{action.FromProcessGrade}->{action.ToProcessGrade}:" +
                $"flags={action.Flags}:reason={action.ReasonMask}"))}");
        var planned = level4Action.Value;
        Assert.Equal(checked((uint)processId), planned.ProcessId);
        Assert.True(planned.Flags.HasFlag(
            NativeSmartCoordinatorActionFlags.RequiresFeedback));
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty(fixture.ProcessPolicyWriter.BatchRequests);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        Assert.Equal(
            NativeSmartCoordinatorProcessGrade.Level4,
            processAfterPlanning.DesiredProcessGrade);
        Assert.Equal(
            NativeSmartCoordinatorProcessGrade.Normal,
            processAfterPlanning.AppliedProcessGrade);
        Assert.Equal(0U, fixture.Workspace.Snapshot.InflightCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalModeRestoresOwnedMemoryPolicyBeforeUnavailableSampling(bool pendingGpu)
    {
        const int processId = 24_049;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processRecoveryRead: requestedProcessId =>
            {
                if (pendingGpu && requestedProcessId == GpuWindowLedgerTestData.Prepared().Window.Request.ProcessId)
                    return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(requestedProcessId,
                        DateTimeOffset.FromFileTime(GpuWindowLedgerTestData.Prepared().Window.Request.CreationFileTimeUtc)));
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal);
        var seeded = await fixture.SeedProcessOwnershipAsync(
            processId,
            processStartKey,
            softwareId,
            memoryPolicy: true,
            actionId: 901);
        if (pendingGpu)
        {
            var call = new ControlledRemoteCall(ResourceManager.App.Domain.GpuPlacement.GpuRemoteCallKind.ReadDevices);
            var fact = new GpuRemoteCallRecord(call.Request).Encode();
            fixture.StateStore.Current = fixture.StateStore.Current with
            {
                AppliedPlacements = [GpuWindowLedgerTestData.Placement(
                    GpuWindowLedgerTestData.Policy(GpuWindowLedgerTestData.Prepared()), fact)]
            };
        }
        var appliedPriorities = new List<uint>();
        var currentMemoryPriority = 3U;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "normal-restore-without-snapshot",
                @"c:\tests\normal-restore-without-snapshot.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            var target = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            appliedPriorities.Add(target);
            currentMemoryPriority = target;
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority restored")])
            ];
        };
        fixture.MetricSampler.ObservationAvailable = false;

        _ = await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        _ = await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        var journal = await fixture.ReadJournalSnapshotAsync();
        Assert.True(
            appliedPriorities.SequenceEqual([5U]),
            $"Expected baseline memory priority 5. recoveryReads=" +
            $"{fixture.ProcessPolicyWriter.RecoveryReadCalls}; " +
            $"batchCalls={fixture.ProcessPolicyWriter.BatchCalls}; " +
            $"ownership={ownership.Records.Count}; " +
            $"journal={journal.Records.Count}; " +
            $"metricReads={fixture.MetricSampler.ReadLatestCalls}; " +
            $"authority={fixture.Coordinator.SchedulingAuthority.Availability}; " +
            $"reason={fixture.Coordinator.SchedulingAuthority.UnavailableReason ?? "none"}.");
        Assert.Equal(pendingGpu ? 4 : 2, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        if (!pendingGpu) Assert.True(fixture.MetricSampler.ReadLatestCalls > 0);
        else
        {
            Assert.True(GpuActionFacts.HasUnsettledActions(fixture.StateStore.Current.AppliedPlacements));
            Assert.Equal(2, Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records.Count);
        }
        Assert.False(File.Exists(seeded.PayloadPath));
        Assert.Empty(ownership.Records);
        Assert.Empty(journal.Records);
        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.Unavailable,
            fixture.Coordinator.SchedulingAuthority.Availability);
    }

    [Fact]
    public async Task RealtimeCyclesApplyThenRestoreMemoryPriorityThroughDurableOwnership()
    {
        const int processId = 24_050;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(processId, processStartKey);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50);
        var currentMemoryPriority = 5U;
        var appliedPriorities = new List<uint>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-cycle",
                @"c:\tests\memory-cycle.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            var target = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            appliedPriorities.Add(target);
            currentMemoryPriority = target;
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U], appliedPriorities);
        var applied = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        var ownership = Assert.Single(applied.Records);
        Assert.Equal(checked((uint)processId), ownership.Primary.ProcessId);
        Assert.Equal(processStartKey, ownership.Primary.ProcessStartKey);

        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 10,
            cpuUsagePercent: 50));
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U, 5U], appliedPriorities);
        Assert.Equal(5U, currentMemoryPriority);
        var restored = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Empty(restored.Records);
    }

    [Fact]
    public async Task RealtimeMemoryPriorityApplyValidationHandoffFailureCallsNoWriter()
    {
        const int processId = 24_055;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            processId,
            processStartKey));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                softwareId,
                baseScore: 20,
                cpuUsagePercent: 50,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50,
            processEffectValidationScopeAuthority: validation.Authority);
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-apply-handoff",
                @"c:\tests\memory-apply-handoff.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5);
        };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        validation.Committer.FailCommitCallNumber =
            validation.Committer.CommitCallCount + 4;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => _ = await fixture.RunRealtimeCycleAsync());

        Assert.Contains("not durably declared", exception.Message);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Contains(
            "handoff-declare-failed",
            validation.Authority.GetStatus().Failure);
    }

    [Fact]
    public async Task RealtimeMemoryPriorityPrePonrRejectionReturnsNewEffectBudget()
    {
        const int processId = 24_057;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var identity = new HostManagerComputeProcessIdentity(processId, processStartKey);
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                CreateSoftwareIdOrderedAfterProcessTarget(processId, processStartKey),
                baseScore: 20,
                cpuUsagePercent: 50,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 99,
            processEffectValidationScopeAuthority: validation.Authority);
        HostManagerProcessEffectValidationAdmissionPermit? blocker = null;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            if (blocker is null)
            {
                Assert.True(validation.Authority.TryBeginAdmission(
                    validation.Authority.Capture(),
                    HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
                    "fresh-apply-pre-ponr",
                    identity,
                    out blocker));
                Assert.NotNull(blocker);
            }
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-pre-ponr-budget",
                @"c:\tests\memory-pre-ponr-budget.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5);
        };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.NotNull(blocker);
        Assert.True(fixture.ProcessPolicyWriter.TotalCalls > 0);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Null(fixture.PublicResourceManager.LastRequest);
        Assert.Equal(0U, fixture.PublicResourceManager.NewEffectAttemptCount);
    }

    [Fact]
    public async Task RealtimeNativeProcessPrePonrRejectionReturnsNewEffectBudget()
    {
        const int processId = 24_059;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var identity = new HostManagerComputeProcessIdentity(processId, processStartKey);
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                CreateSoftwareIdOrderedAfterProcessTarget(processId, processStartKey),
                baseScore: 20,
                cpuUsagePercent: 50,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 99,
            processEffectValidationScopeAuthority: validation.Authority);
        HostManagerProcessEffectValidationAdmissionPermit? blocker = null;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "native-pre-ponr-budget",
                @"c:\tests\native-pre-ponr-budget.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5);
        };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        var nativePlanProbe = new CapturingNativePlanProbe(
            fixture.Workspace,
            actions =>
            {
                if (blocker is not null
                    || !actions.Any(static action =>
                        action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                        && action.Flags.HasFlag(
                            NativeSmartCoordinatorActionFlags.RequiresFeedback)))
                {
                    return;
                }

                Assert.True(validation.Authority.TryBeginAdmission(
                    validation.Authority.Capture(),
                    HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
                    "native-process-policy-pre-ponr",
                    identity,
                    out var admittedBlocker));
                blocker = Assert.IsType<HostManagerProcessEffectValidationAdmissionPermit>(
                    admittedBlocker);
                fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
            });
        fixture.Coordinator.TransitionProbe = nativePlanProbe;

        for (var cycle = 0; cycle < 4 && blocker is null; cycle++)
        {
            if (cycle != 0)
            {
                fixture.PublishNextHostedCpuObservation();
            }
            _ = await fixture.RunRealtimeCycleAsync();
        }

        Assert.True(
            nativePlanProbe.Actions.Any(
                static action => action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                    && action.Flags.HasFlag(
                        NativeSmartCoordinatorActionFlags.RequiresFeedback)),
            string.Join(
                Environment.NewLine,
                nativePlanProbe.Actions.Select(static action =>
                    $"scope={action.Scope}; disposition={action.Disposition}; " +
                    $"process={action.FromProcessGrade}->{action.ToProcessGrade}; " +
                    $"flags={action.Flags}")));
        Assert.NotNull(blocker);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Null(fixture.PublicResourceManager.LastRequest);
        Assert.Equal(0U, fixture.PublicResourceManager.NewEffectAttemptCount);
    }

    [Fact]
    public async Task RealtimeMemoryPriorityRestoreValidationHandoffFailureCallsNoWriter()
    {
        const int processId = 24_056;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            processId,
            processStartKey));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                softwareId,
                baseScore: 20,
                cpuUsagePercent: 50,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        processId,
                        processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50,
            processEffectValidationScopeAuthority: validation.Authority);
        var currentMemoryPriority = 5U;
        Action? processReadHook = null;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            processReadHook?.Invoke();
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-restore-handoff",
                @"c:\tests\memory-restore-handoff.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            currentMemoryPriority = Assert.IsType<uint>(request.MemoryPriority);
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(3U, currentMemoryPriority);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 10,
            cpuUsagePercent: 50));
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");
        validation.Committer.FailCommitCallNumber =
            validation.Committer.CommitCallCount + 4;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => _ = await fixture.RunRealtimeCycleAsync());

        Assert.Contains("not durably declared", exception.Message);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(3U, currentMemoryPriority);
        Assert.Contains(
            "handoff-declare-failed",
            validation.Authority.GetStatus().Failure);
    }

    [Fact]
    public async Task RealtimeMemoryPriorityRestorePrePonrRejectionReturnsRecoveryBudget()
    {
        const int processId = 24_058;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var identity = new HostManagerComputeProcessIdentity(processId, processStartKey);
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                CreateSoftwareIdOrderedAfterProcessTarget(processId, processStartKey),
                baseScore: 20,
                cpuUsagePercent: 50,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50,
            processEffectValidationScopeAuthority: validation.Authority);
        var currentMemoryPriority = 5U;
        Action? processReadHook = null;
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            processReadHook?.Invoke();
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-restore-pre-ponr-budget",
                @"c:\tests\memory-restore-pre-ponr-budget.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            currentMemoryPriority = Assert.IsType<uint>(request.MemoryPriority);
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(3U, currentMemoryPriority);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);

        currentMemoryPriority = 4;
        HostManagerProcessEffectValidationAdmissionPermit? blocker = null;
        processReadHook = () =>
        {
            if (blocker is not null)
            {
                return;
            }
            Assert.True(validation.Authority.TryBeginAdmission(
                validation.Authority.Capture(),
                HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
                "fresh-apply-pre-ponr",
                identity,
                out blocker));
            Assert.NotNull(blocker);
        };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException("The writer must not be called.");

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.NotNull(blocker);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(4U, currentMemoryPriority);
        Assert.Null(fixture.PublicResourceManager.LastRequest);
    }

    [Fact]
    public async Task RealtimeCycleRestoresMemoryPriorityWhenLiveProcessBecomesAdapted()
    {
        const int processId = 24_051;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50);
        var currentMemoryPriority = 5U;
        var appliedPriorities = new List<uint>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-capability-change",
                @"c:\tests\memory-capability-change.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            var target = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            appliedPriorities.Add(target);
            currentMemoryPriority = target;
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([3U], appliedPriorities);

        var adaptedProcess = processFacts.Processes[0] with
        {
            SoftwareKind = SoftwareKinds.Adapted,
            SoftwareDisplayKind = "Adapted",
            SourceGeneration = 2
        };
        fixture.ProcessFacts.SetSnapshot(processFacts with
        {
            Generation = 2,
            ObservedAtUtcTicks = processFacts.ObservedAtUtcTicks + 1,
            Processes = [adaptedProcess]
        });

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U, 5U], appliedPriorities);
        Assert.Equal(5U, currentMemoryPriority);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
    }

    [Fact]
    public async Task RealtimeCycleWaitsForCompleteInventoryThenRestoresWhenAuthorityIsComputeOnly()
    {
        const int processId = 24_052;
        var processStartedAt =
            CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(
            processId,
            processStartKey);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 20,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId =>
            {
                Assert.Equal(processId, requestedProcessId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt));
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50);
        var currentMemoryPriority = 5U;
        var appliedPriorities = new List<uint>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return new ProcessResourcePolicySnapshot(
                processId,
                "memory-authority-loss",
                @"c:\tests\memory-authority-loss.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: currentMemoryPriority);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            var target = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            appliedPriorities.Add(target);
            currentMemoryPriority = target;
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([3U], appliedPriorities);

        fixture.ProcessFacts.SetSnapshot(processFacts with
        {
            InventoryStatus = SamplingObservationStatus.RetainedLastGood
        });
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.Unavailable,
            fixture.Coordinator.SchedulingAuthority.Availability);
        Assert.Equal([3U], appliedPriorities);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);

        var currentProcess = processFacts.Processes[0] with
        {
            SourceGeneration = 2
        };
        fixture.ProcessFacts.SetSnapshot(processFacts with
        {
            Generation = 2,
            ObservedAtUtcTicks = processFacts.ObservedAtUtcTicks + 1,
            Processes = [currentProcess]
        });
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 80,
            cpuUsagePercent: 50,
            memoryUsageAvailable: false));
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            fixture.Coordinator.SchedulingAuthority.Availability);
        Assert.Equal([3U, 5U], appliedPriorities);
        Assert.Equal(5U, currentMemoryPriority);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
    }

    [Fact]
    public async Task RealtimeCycleCompletesOwnedRestoreBeforeLowerRankFreshApply()
    {
        const int restoreProcessId = 24_053;
        const int freshProcessId = 24_054;
        var restoreStartedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        var freshStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var restoreStartKey = checked((ulong)restoreStartedAt.ToFileTime());
        var freshStartKey = checked((ulong)freshStartedAt.ToFileTime());
        const string restoreSoftwareId = "software:restore-first";
        const string freshSoftwareId = "software:fresh-second";
        var initialFacts = CreateCompleteProcessFacts(
            (
                restoreProcessId,
                restoreStartKey,
                restoreSoftwareId,
                20,
                10,
                25),
            (
                freshProcessId,
                freshStartKey,
                freshSoftwareId,
                20,
                80,
                25));
        initialFacts = initialFacts with
        {
            Processes =
            [
                initialFacts.Processes[0],
                initialFacts.Processes[1] with
                {
                    SoftwareKind = SoftwareKinds.Adapted,
                    SoftwareDisplayKind = "Adapted"
                }
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: initialFacts,
            processRecoveryRead: requestedProcessId => requestedProcessId switch
            {
                restoreProcessId =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(
                            restoreProcessId,
                            restoreStartedAt)),
                freshProcessId =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(
                            freshProcessId,
                            freshStartedAt)),
                _ => throw new InvalidOperationException(
                    $"Unexpected process {requestedProcessId}.")
            },
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuUsagePercent: 50);
        var priorities = new Dictionary<int, uint>
        {
            [restoreProcessId] = 5,
            [freshProcessId] = 5
        };
        var writes = new List<(int ProcessId, uint Priority)>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = processId =>
        {
            var startedAt = processId switch
            {
                restoreProcessId => restoreStartedAt,
                freshProcessId => freshStartedAt,
                _ => throw new InvalidOperationException($"Unexpected process {processId}.")
            };
            return new ProcessResourcePolicySnapshot(
                processId,
                $"memory-order-{processId}",
                $@"c:\tests\memory-order-{processId}.exe",
                startedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: priorities[processId]);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            var target = Assert.IsType<uint>(request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            priorities[request.ProcessId] = target;
            writes.Add((request.ProcessId, target));
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([(restoreProcessId, 3U)], writes);

        var currentRestore = initialFacts.Processes[0] with
        {
            CpuUsagePercent = 80,
            SourceGeneration = 2
        };
        var currentFresh = initialFacts.Processes[1] with
        {
            SoftwareKind = "general",
            SoftwareDisplayKind = "General",
            CpuUsagePercent = 10,
            SourceGeneration = 2
        };
        fixture.ProcessFacts.SetSnapshot(initialFacts with
        {
            Generation = 2,
            ObservedAtUtcTicks = initialFacts.ObservedAtUtcTicks + 1,
            Processes = [currentRestore, currentFresh]
        });

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(
            [
                (restoreProcessId, 3U),
                (restoreProcessId, 5U),
                (freshProcessId, 3U)
            ],
            writes);
        Assert.Equal(5U, priorities[restoreProcessId]);
        Assert.Equal(3U, priorities[freshProcessId]);
        var owner = Assert.Single(
            (await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
                CancellationToken.None)).Records);
        Assert.Equal(checked((uint)freshProcessId), owner.Primary.ProcessId);
    }

    [Theory]
    [InlineData(RecoveryReadStatus.Found)]
    [InlineData(RecoveryReadStatus.NotFoundOrExited)]
    [InlineData(RecoveryReadStatus.Unavailable)]
    public async Task RealtimeCycleKeepsLiveCleanupCandidateWhenSampledPeerCannotBeApplied(
        RecoveryReadStatus peerStatus)
    {
        const int unavailableProcessId = 24_101;
        const int liveProcessId = 24_102;
        var unavailableStartedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        var liveStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var unavailableStartKey = checked((ulong)unavailableStartedAt.ToFileTime());
        var liveStartKey = checked((ulong)liveStartedAt.ToFileTime());
        var processFacts = CreateCompleteProcessFacts(
            (
                unavailableProcessId,
                unavailableStartKey,
                "software:unavailable-peer",
                20,
                50,
                25),
            (
                liveProcessId,
                liveStartKey,
                "software:live-peer",
                20,
                50,
                25));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: requestedProcessId => requestedProcessId switch
            {
                unavailableProcessId when peerStatus == RecoveryReadStatus.Found =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(
                            unavailableProcessId,
                            unavailableStartedAt.AddSeconds(1))),
                unavailableProcessId when peerStatus == RecoveryReadStatus.NotFoundOrExited =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                        0,
                        "sampled process exited"),
                unavailableProcessId when peerStatus == RecoveryReadStatus.Unavailable =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                        5,
                        "sampled process is temporarily unreadable"),
                liveProcessId => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(liveProcessId, liveStartedAt)),
                _ => throw new InvalidOperationException("Unexpected process recovery read.")
            });

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.MemoryCleanupPlanner.PlanCalls);
        var request = Assert.IsType<AutomaticMemoryCleanupPlanRequest>(
            fixture.MemoryCleanupPlanner.LastRequest);
        Assert.Equal(2, request.Candidates.Count);
        var unavailable = request.Candidates.Single(
            candidate => candidate.ProcessId == unavailableProcessId);
        var live = request.Candidates.Single(
            candidate => candidate.ProcessId == liveProcessId);
        Assert.False(unavailable.CanApply);
        Assert.Equal(unavailableStartedAt, unavailable.ProcessStartedAt);
        Assert.True(live.CanApply);
        Assert.Equal(liveStartedAt, live.ProcessStartedAt);
        Assert.Equal(3, fixture.ProcessPolicyWriter.RecoveryReadCalls);
    }

    [Fact]
    public void PlacementRestoreValidationKeepsFullAbiActionCapacity()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var plan = new PlacementCoordinatorRuntimePlan(
            hostPlan,
            hostPlan.BuildSpecialize.PlacementCoordinatorAbiVersion,
            hostPlan.HostRecreate.PlacementCoordinator,
            hostPlan.HotPublish.PlacementCoordinator);
        var configuration = new NativePlacementCoordinatorConfiguration
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession
                .SizeOf<NativePlacementCoordinatorConfiguration>(),
            Generation = plan.HotPublish.ConfigurationGeneration,
            MaximumDesiredCount = 1,
            MaximumAppliedCount = 1,
            MaximumActionCount = 4,
            MaximumStateCount = 4,
            RetryDelayMilliseconds = 1,
            ActionTimeoutMilliseconds = checked((ulong)plan.HotPublish.ActionTimeoutMilliseconds),
            MaximumFutureSkewMilliseconds = 1
        };
        var capacity = new NativePlacementCoordinatorCapacity
        {
            StructSize = NativePlacementCoordinatorSession
                .SizeOf<NativePlacementCoordinatorCapacity>(),
            StateCapacity = configuration.MaximumStateCount,
            ActionCapacity = configuration.MaximumActionCount,
            SnapshotStateCapacity = configuration.MaximumStateCount
        };
        var workspace = new NativePlacementCoordinatorWorkspace(
            in configuration,
            in capacity);
        const ulong observedAt = 100;
        var abiActionCapacity = capacity.ActionCapacity;
        var cycle = new NativePlacementCoordinatorCycleInput
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession
                .SizeOf<NativePlacementCoordinatorCycleInput>(),
            ConfigurationGeneration = plan.HotPublish.ConfigurationGeneration,
            CycleEpoch = 1,
            ObservedAtMilliseconds = observedAt,
            ValidMask = (ulong)NativePlacementCycleValidity.Required,
            DesiredCount = 0,
            AppliedCount = 0,
            ActionCapacity = abiActionCapacity,
            ActionCount = 0,
            StateRevision = 1
        };
        var validator = typeof(HostManagerSmartCoordinator).GetMethod(
            "ValidatePlacementCycle",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var exception = Record.Exception(() => validator.Invoke(
            null,
            [cycle, observedAt, 0U, 0U, abiActionCapacity, plan, workspace]));

        Assert.Null(exception);
        Assert.Equal(checked((uint)workspace.Actions.Length), abiActionCapacity);
    }

    private static HostManagerAppliedPlacementReceipt CreateLegacyPlacementReceipt()
    {
        var now = DateTimeOffset.UtcNow;
        return new HostManagerAppliedPlacementReceipt(
            "process:legacy:1",
            "legacy receipt",
            "software:legacy",
            "ProcessPolicy",
            [
                new HostManagerAppliedRecord(
                    HostManagerAppliedRecordKinds.ProcessPriority,
                    "legacy-priority")
            ],
            now,
            now);
    }

    private static AutomaticMemoryCleanupPlanRequest CreateAutomaticMemoryCleanupRequest(
        int candidateCount)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(index => new AutomaticMemoryCleanupCandidate(
                $"process:test:{index}",
                $"test-{index}",
                20_000 + index,
                startedAt.AddSeconds(index),
                "background",
                BaseScore: 10 + index,
                CpuScore: 1,
                MemoryUsedPercent: 10,
                CanApply: true))
            .ToArray();
        return new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal,
            OrdinaryMemoryFreeRatio: 0.05,
            PhysicalMemoryFreeRatio: 0.05,
            VirtualMemoryFreeRatio: 0.5,
            candidates);
    }

    private static AutomaticMemoryCleanupPlanResult CreateAutomaticMemoryCleanupPlan(
        AutomaticMemoryCleanupPlanRequest request)
        => new(
            request.Candidates
                .Select((candidate, index) => new AutomaticMemoryCleanupDecision(
                    index,
                    candidate,
                    AutomaticMemoryCleanupMode.Normal,
                    new AutomaticMemoryCleanupReservation(
                        checked((uint)(index + 1)),
                        StateGeneration: 1)))
                .ToArray(),
            StateRevision: 1);

    private static AutomaticMemoryCleanupPlanResult
        CreateFinalAdmissionAwareMemoryCleanupPlan(
        AutomaticMemoryCleanupPlanRequest request)
    {
        var emergency = request.Kind.HasFlag(
                AutomaticMemoryCleanupRequestKind.EvaluateEmergency)
            && (request.PhysicalMemoryFreeRatio <= 0.06
                || request.VirtualMemoryFreeRatio <= 0.08);
        if (!emergency && request.OrdinaryMemoryFreeRatio >= 0.30)
        {
            return AutomaticMemoryCleanupPlanResult.Empty;
        }

        var decisions = request.Candidates
            .Select((candidate, index) => (candidate, index))
            .Where(static item => item.candidate.CanApply && item.candidate.BaseScore < 81)
            .Select(item => new AutomaticMemoryCleanupDecision(
                item.index,
                item.candidate,
                emergency
                    ? AutomaticMemoryCleanupMode.Emergency
                    : AutomaticMemoryCleanupMode.Normal,
                new AutomaticMemoryCleanupReservation(
                    checked((uint)(item.index + 1)),
                    StateGeneration: 1)))
            .ToArray();
        return decisions.Length == 0
            ? AutomaticMemoryCleanupPlanResult.Empty
            : new AutomaticMemoryCleanupPlanResult(decisions, StateRevision: 1);
    }

    private static void AssertFinalAdmissionDeferredWithoutEffects(
        ScoreOnlyCoordinatorFixture fixture,
        int expectedPlanCalls)
    {
        Assert.True(
            fixture.MemoryCleanupPlanner.PlanCalls == expectedPlanCalls,
            fixture.DescribeMemoryCleanupState());
        Assert.Equal(0, fixture.MemoryCleanupPlanner.CompleteCalls);
        Assert.Empty(fixture.MemoryCleanupPlanner.CompletionHistory);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        fixture.AssertNoMemoryCleanupAttemptEntries();
        Assert.False(fixture.PublicResourceManager.LastRequest!.Value
            .Shortage.MemoryAfterNormalReleaseRounds);
    }

    private static HostManagerMemoryCleanupExecutionResult InvokeExecuteMemoryCleanup(
        HostManagerSmartCoordinator coordinator,
        HostManagerCycleEffectPermit permit,
        AutomaticMemoryCleanupPlanRequest request,
        CancellationToken cancellationToken)
        => (HostManagerMemoryCleanupExecutionResult)typeof(HostManagerSmartCoordinator)
            .GetMethod(
                "ExecuteMemoryCleanup",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                coordinator,
                [permit, request, 1UL, "cycle-budget-test", cancellationToken])!;

    private static void InitializeMemoryCleanupValidationEvidence(
        HostManagerSmartCoordinator coordinator,
        HostManagerProcessEffectValidationCycleSnapshot scope)
    {
        Assert.False(scope.IsProductionUnscoped);
        var ledger = Assert.IsType<HostManagerMemoryCleanupValidationEvidenceLedger>(
            ReadPrivateField<HostManagerMemoryCleanupValidationEvidenceLedger>(
                coordinator,
                "memoryCleanupValidationEvidence"));
        ledger.Reset(scope.ScopeId, scope.Generation);
    }

    private static IReadOnlyList<DurableFileIdentity> CaptureDurableFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DurableFileIdentity(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToArray();

    private static T? ReadPrivateField<T>(object owner, string name)
        where T : class
        => (T?)typeof(HostManagerSmartCoordinator)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner);

    private static void AssertOwnedResourcesCleared(HostManagerSmartCoordinator coordinator)
    {
        Assert.Null(ReadPrivateField<object>(coordinator, "memoryModeWorkspace"));
        Assert.Null(ReadPrivateField<object>(coordinator, "computeScoringWorkspace"));
        Assert.Null(ReadPrivateField<object>(coordinator, "nativeWorkspace"));
        Assert.Null(ReadPrivateField<object>(coordinator, "placementCoordinatorSession"));
        Assert.Null(ReadPrivateField<object>(coordinator, "placementCoordinatorWorkspace"));
    }

    private static void WritePrivateField<T>(
        HostManagerSmartCoordinator owner,
        string name,
        T value)
        => typeof(HostManagerSmartCoordinator)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, value);

    private static T ReadPrivateValue<T>(object owner, string name)
        where T : struct
        => (T)typeof(HostManagerSmartCoordinator)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;

    private static object? ReadPrivateReference(object owner, string name)
        => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner);

    private sealed class GpuPolicyEnvironment(string root) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GpuPolicyTests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed record DurableFileIdentity(
        string RelativePath,
        long Length,
        string Sha256);

    private sealed record SeededProcessOwnership(
        NativeAppliedOwnershipPrimaryIdentity Primary,
        string PayloadPath);

    private sealed class BlockingTransitionProbe(
        HostManagerSmartCoordinatorTransitionPoint target)
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        private readonly TaskCompletionSource reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource admissionClosed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object shutdownSync = new();
        private readonly List<HostManagerSmartCoordinatorShutdownPoint> shutdownPoints = [];
        private int observed;

        internal Task Reached => reached.Task;
        internal Task AdmissionClosed => admissionClosed.Task;

        public void Reach(HostManagerSmartCoordinatorTransitionPoint point)
        {
            if (point != target || Interlocked.Exchange(ref observed, 1) != 0)
            {
                return;
            }

            reached.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point)
        {
            lock (shutdownSync)
            {
                shutdownPoints.Add(point);
            }
            if (point == HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed)
            {
                admissionClosed.TrySetResult();
            }
        }

        internal void Release() => release.TrySetResult();

        internal void AssertShutdownOrder()
        {
            HostManagerSmartCoordinatorShutdownPoint[] snapshot;
            lock (shutdownSync)
            {
                snapshot = [.. shutdownPoints];
            }
            Assert.Equal(
                [
                    HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed,
                    HostManagerSmartCoordinatorShutdownPoint.ExactWorkerJoined,
                    HostManagerSmartCoordinatorShutdownPoint.GateDrained,
                    HostManagerSmartCoordinatorShutdownPoint.OwnedResourcesDisposed
                ],
                snapshot);
        }
    }

    private sealed class CapturingNativePlanProbe(
        NativeSmartCoordinatorWorkspace workspace,
        Action<NativeSmartCoordinatorAction[]>? afterCapture = null)
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        internal NativeSmartCoordinatorSnapshot Snapshot { get; private set; }
        internal NativeSmartCoordinatorAction[] Actions { get; private set; } = [];

        public void Reach(HostManagerSmartCoordinatorTransitionPoint point)
        {
            if (point != HostManagerSmartCoordinatorTransitionPoint.AfterNativeCall)
            {
                return;
            }

            Snapshot = workspace.Snapshot;
            Actions = workspace.PlannedActions.ToArray();
            afterCapture?.Invoke(Actions);
        }

        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point)
        {
        }
    }

    private sealed class ThrowOnceTransitionProbe(
        HostManagerSmartCoordinatorTransitionPoint target)
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        private bool shouldThrow = true;

        public void Reach(HostManagerSmartCoordinatorTransitionPoint point)
        {
            if (point != target || !shouldThrow)
            {
                return;
            }

            shouldThrow = false;
            throw new InvalidOperationException("simulated recreate failure");
        }

        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point)
        {
        }
    }

    private sealed class ScoreOnlyCoordinatorFixture : IAsyncDisposable
    {
        private readonly HostManagerMemoryCleanupAttemptJournal memoryCleanupJournal;
        private readonly IDisposable? ownedProcessFactsOverride;

        private ScoreOnlyCoordinatorFixture(
            string root,
            CompiledRuntimePlan runtimePlan,
            RuntimePlanProvider runtimePlanProvider,
            HostManagerDeploymentState deploymentState,
            RecordingRollbackStateStore stateStore,
            RecordingMetricSampler metricSampler,
            RecordingProcessFactSource processFacts,
            AllowingControlZones controlZones,
            RecordingProcessPolicyWriter processPolicyWriter,
            RecordingAdapterPolicyDispatcher adapterDispatcher,
            RecordingAutomaticMemoryCleanupPlanner memoryCleanupPlanner,
            RecordingPublicResourceManager publicResourceManager,
            RecordingProtectionService protectionService,
            RecordingGraphicsPreferenceStore graphicsPreferenceStore,
            RecordingDebugLogWriter debugLogWriter,
            HostManagerMemoryCleanupAttemptJournal memoryCleanupJournal,
            HostManagerNativeActionTransactionRuntime nativeTransactions,
            HostManagerSmartCoordinator coordinator,
            NativeSmartCoordinatorWorkspace workspace,
            IDisposable? ownedProcessFactsOverride)
        {
            Root = root;
            RuntimePlan = runtimePlan;
            RuntimePlanProvider = runtimePlanProvider;
            DeploymentState = deploymentState;
            StateStore = stateStore;
            MetricSampler = metricSampler;
            ProcessFacts = processFacts;
            ControlZones = controlZones;
            ProcessPolicyWriter = processPolicyWriter;
            AdapterDispatcher = adapterDispatcher;
            MemoryCleanupPlanner = memoryCleanupPlanner;
            PublicResourceManager = publicResourceManager;
            ProtectionService = protectionService;
            GraphicsPreferenceStore = graphicsPreferenceStore;
            DebugLogWriter = debugLogWriter;
            this.memoryCleanupJournal = memoryCleanupJournal;
            NativeTransactions = nativeTransactions;
            Coordinator = coordinator;
            Workspace = workspace;
            this.ownedProcessFactsOverride = ownedProcessFactsOverride;
            MemoryCleanupJournalPath = Path.Combine(root, "memory-cleanup-attempts.json");
        }

        internal string Root { get; }
        internal string MemoryCleanupJournalPath { get; }
        internal HostManagerMemoryCleanupAttemptJournal MemoryCleanupJournal
            => memoryCleanupJournal;
        internal CompiledRuntimePlan RuntimePlan { get; }
        internal RuntimePlanProvider RuntimePlanProvider { get; }
        internal HostManagerDeploymentState DeploymentState { get; }
        internal RecordingRollbackStateStore StateStore { get; }
        internal RecordingMetricSampler MetricSampler { get; }
        internal RecordingProcessFactSource ProcessFacts { get; }
        internal AllowingControlZones ControlZones { get; }
        internal RecordingProcessPolicyWriter ProcessPolicyWriter { get; }
        internal RecordingAdapterPolicyDispatcher AdapterDispatcher { get; }
        internal RecordingAutomaticMemoryCleanupPlanner MemoryCleanupPlanner { get; }
        internal RecordingPublicResourceManager PublicResourceManager { get; }
        internal RecordingProtectionService ProtectionService { get; }
        internal RecordingGraphicsPreferenceStore GraphicsPreferenceStore { get; }
        internal RecordingDebugLogWriter DebugLogWriter { get; }
        internal HostManagerNativeActionTransactionRuntime NativeTransactions { get; }
        internal HostManagerSmartCoordinator Coordinator { get; }
        internal NativeSmartCoordinatorWorkspace Workspace { get; }

        internal static async Task<ScoreOnlyCoordinatorFixture> CreateAsync(
            bool warm,
            bool scoreOnlyEnabled = true,
            SchedulingProcessFactSnapshot? processFactsSnapshot = null,
            Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>>? processRecoveryRead = null,
            bool policyExecutionEnabled = true,
            bool memoryModePolicyEnabled = false,
            bool automaticMemoryCleanupEnabled = true,
            bool performanceLogEnabled = false,
            double cpuUsagePercent = 50,
            int? memoryCleanupStateCapacity = null,
            bool memoryUsageAvailable = true,
            string optimizationMode = AppOptimizationModes.Smart,
            TimeProvider? timeProvider = null,
             HostManagerProcessEffectValidationScopeAuthority?
                 processEffectValidationScopeAuthority = null,
             int? smartCoordinatorMaximumProcesses = null,
             IDebugDiagnosticLogWriter? forwardingDebugLogWriter = null,
             OptimizationModeCapabilities? optimizationCapabilities = null,
             IMetricSnapshotObservationSource? metricSamplerOverride = null,
             ISchedulingProcessFactObservationSource? processFactsOverride = null,
             Func<IRuntimePlanProvider, ISchedulingProcessFactObservationSource>?
                 processFactsFactory = null,
             int? normalIntervalMilliseconds = null,
             int? eventIntervalMilliseconds = null,
             bool failFastEffects = false,
             Action<System.Text.Json.Nodes.JsonObject>? editFreedomPoints = null,
             ResourceManager.App.Application.CpuTopology.ICpuCoreResidencyReader? cpuCoreReader = null,
             ResourceManager.App.Domain.CpuTopology.CpuTopologySnapshot? cpuTopology = null,
             CompiledCpuScoringPlan? cpuScoring = null,
             ResourceManager.App.Domain.CpuTopology.CpuTopologySnapshot?
                 automaticPlacementTopology = null,
             IHostManagerRollbackStateStore? rollbackStateStoreOverride = null,
             ResourceManager.App.Application.GpuPlacement.IRunningGpuPlacementActionService? runningGpuActions = null,
             IWindowsGraphicsPreferenceStore? graphicsPreferenceOverride = null,
             Func<string, IRuntimePlanProvider, ResourceManager.App.Application.GpuPlacement.IRunningGpuPlacementActionService>?
                 runningGpuActionsFactory = null,
             ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution.WindowsGpuWindowActionRuntime? gpuWindowRuntime = null,
             ResourceManager.App.Infrastructure.GpuPlacement.Preparation.WindowsGpuCallbackPreparationRuntime? gpuCallbackRuntime = null,
             Microsoft.Extensions.Logging.ILogger<HostManagerSmartCoordinator>? coordinatorLogger = null,
             ResourceManagerSelfLocalResourceManager? selfLocalResourceManager = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "resource-manager-score-only",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var deployment = new HostManagerDeploymentState();
            var runtimePlanProvider = new RuntimePlanProvider(deployment);
            var performance = AppSettingsDefaults.Create().Performance with
            {
                OptimizationMode = optimizationMode
            };
            var hostPlan = HostManagerTestPlanFactory.CreatePlan(
                root =>
                {
                    if (smartCoordinatorMaximumProcesses is not null)
                    {
                        root["host_recreate"]!["smart_coordinator"]!
                            ["maximum_processes"] = smartCoordinatorMaximumProcesses.Value;
                    }
                    if (normalIntervalMilliseconds is not null)
                    {
                        root["hot_publish"]!["smart_coordinator"]!
                            ["normal_interval_ms"] = normalIntervalMilliseconds.Value;
                    }
                    if (eventIntervalMilliseconds is not null)
                    {
                        root["hot_publish"]!["smart_coordinator"]!
                            ["event_interval_ms"] = eventIntervalMilliseconds.Value;
                    }
                },
                performance,
                editFreedomPoints: editFreedomPoints,
                cpuTopology: cpuTopology,
                cpuScoring: cpuScoring);
            if (memoryCleanupStateCapacity is not null)
            {
                hostPlan = hostPlan with
                {
                    HostRecreate = hostPlan.HostRecreate with
                    {
                        MemoryCleanup = new CompiledHostManagerMemoryCleanupRecreatePlan(
                            memoryCleanupStateCapacity.Value)
                    }
                };
            }
            var optimizationModePlan = CompiledOptimizationModePlan.Compile(
                optimizationMode);
            if (!automaticMemoryCleanupEnabled)
            {
                optimizationModePlan = optimizationModePlan with
                {
                    Capabilities = optimizationModePlan.Capabilities
                        & ~OptimizationModeCapabilities.AutomaticMemoryCleanup
                };
            }
            if (optimizationCapabilities is not null)
            {
                optimizationModePlan = optimizationModePlan with
                {
                    Capabilities = optimizationCapabilities.Value
                };
            }
            var runtimePlan = CompiledRuntimePlan.Default with
            {
                Version = 1,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "score-only-composition-test",
                HostManager = hostPlan,
                 OptimizationMode = optimizationModePlan,
                 CpuPlacementTopology = automaticPlacementTopology,
                 Diagnostics = CompiledDiagnosticsPlan.Default with
                {
                    DebugModeEnabled = performanceLogEnabled,
                    DebugLogEnabled = performanceLogEnabled,
                    HostManagerSmartCoordinatorScoreOnlyEnabled = scoreOnlyEnabled,
                    HostManagerSmartCoordinatorPerformanceLogEnabled = performanceLogEnabled
                }
            };
            runtimePlanProvider.Publish(runtimePlan);

            var stateStore = new RecordingRollbackStateStore(
                HostManagerRollbackStateDocument.Empty with
                {
                    NativeHostSessionIncarnation = warm ? 1UL : 0UL
                })
            {
                FailOnEffectCall = failFastEffects
            };
            var metricSampler = new RecordingMetricSampler(
                CreateHardwareSnapshot(
                    memoryModePolicyEnabled ? 80 : 99,
                    cpuUsagePercent,
                    memoryUsageAvailable),
                hostPlan.DataHistory);
            var initialProcessFacts = processFactsSnapshot ?? CreateProcessFacts();
            if (initialProcessFacts.RequestedMetricMask.HasFlag(
                    SchedulingProcessMetricMask.MemoryUsage)
                && SystemMemoryUsageDependency.TryCreate(
                    metricSampler.Snapshot,
                    DateTimeOffset.UtcNow,
                    out _))
            {
                initialProcessFacts = CreateHostedFinalAdmissionProcessFacts(
                    initialProcessFacts,
                    metricSampler.Snapshot);
            }
            var processFacts = new RecordingProcessFactSource(initialProcessFacts);
            var controlZones = new AllowingControlZones(policyExecutionEnabled);
            var processPolicyWriter = new RecordingProcessPolicyWriter(processRecoveryRead);
            var adapterDispatcher = new RecordingAdapterPolicyDispatcher();
            var memoryCleanupPlanner = new RecordingAutomaticMemoryCleanupPlanner
                (hostPlan.HotPublish.MemoryCleanup.ConfigurationGeneration)
            {
                FailOnCall = failFastEffects
            };
            var publicResourceManager = new RecordingPublicResourceManager
            {
                FailOnCall = failFastEffects
            };
            var protectionService = new RecordingProtectionService();
            var graphicsPreferenceStore = new RecordingGraphicsPreferenceStore();
            var debugLogWriter = new RecordingDebugLogWriter(forwardingDebugLogWriter);
            if (processFactsOverride is not null && processFactsFactory is not null)
            {
                throw new ArgumentException(
                    "A score-only process-fact override and factory cannot both be supplied.");
            }
            var factoryProcessFacts = processFactsFactory?.Invoke(runtimePlanProvider);
            var selectedProcessFacts = processFactsOverride
                ?? factoryProcessFacts
                ?? processFacts;
            timeProvider ??= TimeProvider.System;
            var memoryCleanupJournalPath = Path.Combine(root, "memory-cleanup-attempts.json");
            var memoryCleanupJournal = new HostManagerMemoryCleanupAttemptJournal(
                memoryCleanupJournalPath,
                () => hostPlan.HostRecreate.MemoryCleanup.StateCapacity,
                timeProvider);
            var retirements = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);
            var nativeTransactions = new HostManagerNativeActionTransactionRuntime(
                new HostManagerTransactionJournalDeploymentRuntime(
                    runtimePlanProvider,
                    deployment),
                new HostManagerAppliedOwnershipRuntime(
                    new HostManagerAppliedOwnershipDeploymentRuntime(
                        runtimePlanProvider,
                        deployment),
                    retirements),
                retirements);
            if (warm)
            {
                await nativeTransactions.EnsureReadyAsync(CancellationToken.None);
            }

            var smartRuntime = new HostManagerSmartCoordinatorRuntime(deployment);
            NativeSmartCoordinatorWorkspace workspace;
            HostManagerSmartCoordinatorRuntimePlan desired;
            using (var lease = runtimePlanProvider.AcquirePublicationLease())
            {
                desired = smartRuntime.CaptureDesired(lease);
                var configuration = desired.Configuration;
                workspace = warm
                    ? new NativeSmartCoordinatorWorkspace(in configuration)
                    : null!;
            }
            var coordinator = new HostManagerSmartCoordinator(
                settingsStore: null!,
                metricSamplerOverride ?? metricSampler,
                selectedProcessFacts,
                cpuCoreReader ?? new RecordingCpuCoreResidencyReader(() => CpuCoreResidencyTestValues.OneCore(processFacts.Snapshot)),
                controlZones,
                rollbackStateStoreOverride ?? stateStore,
                protectionService,
                runtimePlanProvider,
                runtimeSpecializationCoordinator: null!,
                smartRuntime,
                new HostManagerPlacementCoordinatorRuntime(runtimePlanProvider, deployment),
                memoryCleanupPlanner,
                memoryCleanupJournal,
                processPolicyWriter,
                new HostManagerProcessPolicyTransaction(processPolicyWriter),
                new HostManagerAdapterSchedulingTransaction(adapterDispatcher, timeProvider),
                nativeTransactions,
                timeProvider,
                graphicsPreferenceOverride ?? graphicsPreferenceStore,
                new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(root)),
                gpuWindowRuntime ?? new ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution.WindowsGpuWindowActionRuntime(),
                gpuCallbackRuntime ?? new ResourceManager.App.Infrastructure.GpuPlacement.Preparation.WindowsGpuCallbackPreparationRuntime(),
                runningGpuActions ?? runningGpuActionsFactory?.Invoke(root, runtimePlanProvider) ?? new RecordingRunningGpuActions(),
                debugLogWriter,
                coordinatorLogger ?? NullLogger<HostManagerSmartCoordinator>.Instance,
                selfLocalResourceManager ?? (ResourceManagerSelfLocalResourceManager)RuntimeHelpers.GetUninitializedObject(
                    typeof(ResourceManagerSelfLocalResourceManager)),
                publicResourceManager,
                new HostManagerSchedulingAuthority(),
                new HostManagerMemoryModePolicyAuthority(
                    memoryModePolicyEnabled
                        ? new ProductBaselineHostManagerMemoryModePolicySource()
                        : new UnavailableMemoryModePolicySource()),
                processEffectValidationScopeAuthority
                    ?? new HostManagerProcessEffectValidationScopeAuthority(
                        Path.Combine(
                            root,
                            "validation-scope",
                            "process-effect-validation-scope.json"),
                        timeProvider,
                        new WindowsJobMembershipProbe(),
                        WindowsHostManagerProcessEffectValidationScopeFileCommitter.Instance));

            if (warm)
            {
                var attempt = smartRuntime.BeginInitialCreate(hostPlan);
                Assert.Equal(
                    HostManagerDeploymentAttemptSettlement.Applied,
                    smartRuntime.CompleteSucceeded(attempt));
                WritePrivateField(coordinator, "nativeWorkspace", workspace);
                WritePrivateField(coordinator, "appliedNativeRuntimePlan", desired);
                WritePrivateField(coordinator, "nativeHostSessionIncarnation", 1UL);
            }

            return new ScoreOnlyCoordinatorFixture(
                root,
                runtimePlan,
                runtimePlanProvider,
                deployment,
                stateStore,
                metricSampler,
                processFacts,
                controlZones,
                processPolicyWriter,
                adapterDispatcher,
                memoryCleanupPlanner,
                publicResourceManager,
                protectionService,
                graphicsPreferenceStore,
                debugLogWriter,
                memoryCleanupJournal,
                nativeTransactions,
                coordinator,
                workspace,
                factoryProcessFacts as IDisposable);
        }

        internal async Task<SeededProcessOwnership> SeedProcessOwnershipAsync(
            int processId,
            ulong processStartKey,
            string softwareId,
            bool memoryPolicy,
            ulong actionId)
        {
            Assert.True(NativeTransactions.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await using (admission)
            {
                if (admission!.PayloadReconciliationRequired)
                {
                    _ = await admission.ReconcilePayloadsAsync(
                        ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                        CancellationToken.None);
                }

                var journal = await admission.ReadSnapshotAsync(CancellationToken.None);
                var targetId = memoryPolicy
                    ? HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                        processId,
                        processStartKey)
                    : HostManagerTargetIdentity.CreateProcessTargetId(
                        processId,
                        processStartKey);
                var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(targetId);
                var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(softwareId);
                var actionIdentity = new NativeTransactionJournalIdentity
                {
                    ConfigurationGeneration = NativeTransactions.CurrentJournalPlan
                        .HotPublish.ConfigurationGeneration,
                    PlanEpoch = 1,
                    ActionId = actionId,
                    HostSessionIncarnation = 1,
                    TargetId = targetKey,
                    SoftwareId = softwareKey,
                    ProcessStartKey = processStartKey,
                    ProcessId = checked((uint)processId)
                };
                var domain = memoryPolicy
                    ? NativeTransactionJournalDomain.PhysicalMemory
                    : NativeTransactionJournalDomain.Process;
                var gradeValidity = memoryPolicy
                    ? NativeTransactionJournalGradeValidity.Memory
                    : NativeTransactionJournalGradeValidity.Process;
                var fromGrade = memoryPolicy
                    ? 0
                    : (int)NativeTransactionJournalProcessGrade.Normal;
                var toGrade = memoryPolicy
                    ? 3
                    : (int)NativeTransactionJournalProcessGrade.Level2;
                var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var binding = new NativeTransactionJournalPayloadBinding(
                    journal.Header.JournalInstanceLow,
                    journal.Header.JournalInstanceHigh,
                    actionIdentity,
                    NativeTransactionJournalScope.Process,
                    NativeTransactionJournalDisposition.Apply,
                    domain,
                    gradeValidity,
                    fromGrade,
                    toGrade,
                    CpuFromGrade: 0,
                    CpuToGrade: 0,
                    GpuFromGrade: 0,
                    GpuToGrade: 0,
                    StableSystemStatus: 0,
                    StableSystemError: 0,
                    MaximumRecoveryAttempts: 3,
                    RecoveryDeadlineUtcMilliseconds: now + 60_000,
                    AtomicGroupId: 0,
                    GroupMemberIndex: 0,
                    GroupMemberCount: 0);
                Assert.True(binding.IsValid);
                var payload = memoryPolicy
                    ? HostManagerProcessPolicyRollbackPayloadCodec.Encode(
                        new HostManagerProcessPolicyRollbackPayload(
                            HostManagerProcessPolicyTransactionFields.MemoryPriority,
                            processId,
                            checked((long)processStartKey),
                            BaselinePriorityClass: 0,
                            BaselineMemoryPriority: 5,
                            BaselinePowerControlMask: 0,
                            BaselinePowerStateMask: 0,
                            TargetMemoryPriority: 3))
                    : BitConverter.GetBytes(actionId);
                var payloadReference = await admission.PersistPayloadAsync(
                    binding,
                    payload,
                    CancellationToken.None);
                var ownership = await NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
                    CancellationToken.None);
                var primary = new NativeAppliedOwnershipPrimaryIdentity
                {
                    Scope = (uint)NativeAppliedOwnershipScope.Process,
                    TargetId = targetKey,
                    SoftwareId = softwareKey,
                    ProcessStartKey = processStartKey,
                    ProcessId = checked((uint)processId)
                };
                var promote = new NativeAppliedOwnershipPromoteInput
                {
                    AbiVersion = NativeAppliedOwnershipAbi.Version,
                    StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
                    ExpectedLedgerRevision = ownership.Header.LedgerRevision,
                    Primary = primary,
                    OriginalBinding = new NativeAppliedOwnershipOriginalBinding
                    {
                        JournalInstanceLow = binding.JournalInstanceLow,
                        JournalInstanceHigh = binding.JournalInstanceHigh,
                        ActionIdentity = new NativeAppliedOwnershipActionIdentity
                        {
                            ConfigurationGeneration = actionIdentity.ConfigurationGeneration,
                            PlanEpoch = actionIdentity.PlanEpoch,
                            ActionId = actionIdentity.ActionId,
                            HostSessionIncarnation = actionIdentity.HostSessionIncarnation,
                            TargetId = actionIdentity.TargetId,
                            SoftwareId = actionIdentity.SoftwareId,
                            ProcessStartKey = actionIdentity.ProcessStartKey,
                            ProcessId = actionIdentity.ProcessId
                        },
                        Scope = (uint)binding.Scope,
                        Disposition = (uint)binding.Disposition,
                        DomainMask = (uint)binding.Domain,
                        GradeValidMask = (uint)binding.GradeValidMask,
                        ProcessFromGrade = binding.ProcessFromGrade,
                        ProcessToGrade = binding.ProcessToGrade,
                        MaximumRecoveryAttempts = binding.MaximumRecoveryAttempts,
                        RecoveryDeadlineUtcMilliseconds = binding.RecoveryDeadlineUtcMilliseconds
                    },
                    Payload = new NativeAppliedOwnershipDurablePayloadReference
                    {
                        Slot = payloadReference.Slot,
                        Generation = payloadReference.Generation,
                        Length = payloadReference.Length,
                        DigestLow = payloadReference.DigestLow,
                        DigestHigh = payloadReference.DigestHigh
                    },
                    CurrentGrades = new NativeAppliedOwnershipCurrentGrades
                    {
                        ValidMask = (uint)gradeValidity,
                        ProcessGrade = toGrade
                    },
                    PromotedAtUtcMilliseconds = now
                };
                Assert.Equal(
                    NativeAppliedOwnershipStatus.Ok,
                    await NativeTransactions.AppliedOwnership.PromoteAsync(
                        promote,
                        CancellationToken.None));
                var payloadPath = Assert.Single(Directory.EnumerateFiles(
                    Root,
                    $"payload-{payloadReference.Generation:D10}-{payloadReference.Slot:D10}.bin",
                    SearchOption.AllDirectories));
                return new SeededProcessOwnership(primary, payloadPath);
            }
        }

        internal async Task SeedAdapterOwnershipAsync(
            string softwareId,
            ulong actionId)
        {
            Assert.True(NativeTransactions.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await using (admission)
            {
                if (admission!.PayloadReconciliationRequired)
                {
                    _ = await admission.ReconcilePayloadsAsync(
                        ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                        CancellationToken.None);
                }

                var journal = await admission.ReadSnapshotAsync(CancellationToken.None);
                var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(softwareId);
                var actionIdentity = new NativeTransactionJournalIdentity
                {
                    ConfigurationGeneration = NativeTransactions.CurrentJournalPlan
                        .HotPublish.ConfigurationGeneration,
                    PlanEpoch = 1,
                    ActionId = actionId,
                    HostSessionIncarnation = 1,
                    TargetId = softwareKey,
                    SoftwareId = softwareKey
                };
                var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var binding = new NativeTransactionJournalPayloadBinding(
                    journal.Header.JournalInstanceLow,
                    journal.Header.JournalInstanceHigh,
                    actionIdentity,
                    NativeTransactionJournalScope.Software,
                    NativeTransactionJournalDisposition.Apply,
                    NativeTransactionJournalDomain.Cpu |
                        NativeTransactionJournalDomain.Gpu,
                    NativeTransactionJournalGradeValidity.Cpu |
                        NativeTransactionJournalGradeValidity.Gpu,
                    ProcessFromGrade: 0,
                    ProcessToGrade: 0,
                    CpuFromGrade: (int)NativeTransactionJournalAdapterGrade.Normal,
                    CpuToGrade: (int)NativeTransactionJournalAdapterGrade.Optimize,
                    GpuFromGrade: (int)NativeTransactionJournalAdapterGrade.Normal,
                    GpuToGrade: (int)NativeTransactionJournalAdapterGrade.Extreme,
                    StableSystemStatus: 0,
                    StableSystemError: 0,
                    MaximumRecoveryAttempts: 3,
                    RecoveryDeadlineUtcMilliseconds: now + 60_000,
                    AtomicGroupId: 0,
                    GroupMemberIndex: 0,
                    GroupMemberCount: 0);
                Assert.True(binding.IsValid);
                var payloadReference = await admission.PersistPayloadAsync(
                    binding,
                    BitConverter.GetBytes(actionId),
                    CancellationToken.None);
                var ownership = await NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
                    CancellationToken.None);
                var promote = new NativeAppliedOwnershipPromoteInput
                {
                    AbiVersion = NativeAppliedOwnershipAbi.Version,
                    StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
                    ExpectedLedgerRevision = ownership.Header.LedgerRevision,
                    Primary = new NativeAppliedOwnershipPrimaryIdentity
                    {
                        Scope = (uint)NativeAppliedOwnershipScope.Adapter,
                        SoftwareId = softwareKey
                    },
                    OriginalBinding = new NativeAppliedOwnershipOriginalBinding
                    {
                        JournalInstanceLow = binding.JournalInstanceLow,
                        JournalInstanceHigh = binding.JournalInstanceHigh,
                        ActionIdentity = new NativeAppliedOwnershipActionIdentity
                        {
                            ConfigurationGeneration = actionIdentity.ConfigurationGeneration,
                            PlanEpoch = actionIdentity.PlanEpoch,
                            ActionId = actionIdentity.ActionId,
                            HostSessionIncarnation = actionIdentity.HostSessionIncarnation,
                            TargetId = actionIdentity.TargetId,
                            SoftwareId = actionIdentity.SoftwareId
                        },
                        Scope = (uint)binding.Scope,
                        Disposition = (uint)binding.Disposition,
                        DomainMask = (uint)binding.Domain,
                        GradeValidMask = (uint)binding.GradeValidMask,
                        CpuFromGrade = binding.CpuFromGrade,
                        CpuToGrade = binding.CpuToGrade,
                        GpuFromGrade = binding.GpuFromGrade,
                        GpuToGrade = binding.GpuToGrade,
                        MaximumRecoveryAttempts = binding.MaximumRecoveryAttempts,
                        RecoveryDeadlineUtcMilliseconds =
                            binding.RecoveryDeadlineUtcMilliseconds
                    },
                    Payload = new NativeAppliedOwnershipDurablePayloadReference
                    {
                        Slot = payloadReference.Slot,
                        Generation = payloadReference.Generation,
                        Length = payloadReference.Length,
                        DigestLow = payloadReference.DigestLow,
                        DigestHigh = payloadReference.DigestHigh
                    },
                    CurrentGrades = new NativeAppliedOwnershipCurrentGrades
                    {
                        ValidMask = (uint)binding.GradeValidMask,
                        CpuGrade = binding.CpuToGrade,
                        GpuGrade = binding.GpuToGrade
                    },
                    PromotedAtUtcMilliseconds = now
                };
                Assert.Equal(
                    NativeAppliedOwnershipStatus.Ok,
                    await NativeTransactions.AppliedOwnership.PromoteAsync(
                        promote,
                        CancellationToken.None));
            }
        }

        internal void PublishSmartCoordinatorRecreatePlan()
        {
            var performance = AppSettingsDefaults.Create().Performance with
            {
                OptimizationMode = AppOptimizationModes.Smart
            };
            var nextHostPlan = HostManagerTestPlanFactory.CreatePlan(
                static root =>
                {
                    root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
                    var smartCoordinator = root["host_recreate"]!.AsObject()
                        ["smart_coordinator"]!.AsObject();
                    smartCoordinator["maximum_processes"] = 4097;
                    smartCoordinator["maximum_reservations"] = 8193;
                    smartCoordinator["maximum_atomic_groups"] = 4097;
                },
                performance,
                planEpoch: 2);
            RuntimePlanProvider.Publish(RuntimePlan with
            {
                Version = 2,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "lifecycle-recreate-test",
                HostManager = nextHostPlan
            });
        }

        internal async Task PreparePendingTransactionAsync(
            NativeTransactionJournalScope scope = NativeTransactionJournalScope.Process)
        {
            Assert.True(NativeTransactions.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await using (admission)
            {
                var snapshot = await admission!.ReadSnapshotAsync(CancellationToken.None);
                var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var prepare = new NativeTransactionJournalPrepareInput
                {
                    AbiVersion = NativeTransactionJournalAbi.Version,
                    StructSize = NativeTransactionJournalSession
                        .SizeOf<NativeTransactionJournalPrepareInput>(),
                    ExpectedJournalRevision = snapshot.Header.JournalRevision,
                    Identity = new NativeTransactionJournalIdentity
                    {
                        ConfigurationGeneration = NativeTransactions.CurrentJournalPlan
                            .HotPublish.ConfigurationGeneration,
                        PlanEpoch = 1,
                        ActionId = 1,
                        HostSessionIncarnation = 1,
                        TargetId = 11,
                        SoftwareId = 12,
                        ProcessStartKey = scope == NativeTransactionJournalScope.Process
                            ? 13UL
                            : 0,
                        ProcessId = scope == NativeTransactionJournalScope.Process
                            ? 14U
                            : 0
                    },
                    Scope = (uint)scope,
                    Disposition = (uint)NativeTransactionJournalDisposition.Apply,
                    DomainMask = (uint)(scope switch
                    {
                        NativeTransactionJournalScope.Process =>
                            NativeTransactionJournalDomain.Process,
                        NativeTransactionJournalScope.Software =>
                            NativeTransactionJournalDomain.Cpu |
                                NativeTransactionJournalDomain.Gpu,
                        NativeTransactionJournalScope.Resource =>
                            NativeTransactionJournalDomain.PhysicalMemory |
                                NativeTransactionJournalDomain.SharedResource,
                        _ => throw new ArgumentOutOfRangeException(nameof(scope))
                    }),
                    GradeValidMask = (uint)(scope switch
                    {
                        NativeTransactionJournalScope.Process =>
                            NativeTransactionJournalGradeValidity.Process,
                        NativeTransactionJournalScope.Software =>
                            NativeTransactionJournalGradeValidity.Cpu |
                                NativeTransactionJournalGradeValidity.Gpu,
                        NativeTransactionJournalScope.Resource =>
                            NativeTransactionJournalGradeValidity.None,
                        _ => throw new ArgumentOutOfRangeException(nameof(scope))
                    }),
                    ProcessFromGrade = scope == NativeTransactionJournalScope.Process
                        ? (int)NativeTransactionJournalProcessGrade.Normal
                        : 0,
                    ProcessToGrade = scope == NativeTransactionJournalScope.Process
                        ? (int)NativeTransactionJournalProcessGrade.Level1
                        : 0,
                    CpuFromGrade = scope == NativeTransactionJournalScope.Software
                        ? (int)NativeTransactionJournalAdapterGrade.Normal
                        : 0,
                    CpuToGrade = scope == NativeTransactionJournalScope.Software
                        ? (int)NativeTransactionJournalAdapterGrade.Optimize
                        : 0,
                    GpuFromGrade = scope == NativeTransactionJournalScope.Software
                        ? (int)NativeTransactionJournalAdapterGrade.Normal
                        : 0,
                    GpuToGrade = scope == NativeTransactionJournalScope.Software
                        ? (int)NativeTransactionJournalAdapterGrade.Extreme
                        : 0,
                    PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
                    PayloadSlot = 1,
                    PayloadGeneration = 1,
                    PayloadLength = 64,
                    PayloadDigestLow = 101,
                    PayloadDigestHigh = 102,
                    PayloadProvenanceDigestLow = 103,
                    PayloadProvenanceDigestHigh = 104,
                    NowUtcMilliseconds = now,
                    MaximumRecoveryAttempts = 3,
                    RecoveryDeadlineUtcMilliseconds = now + 60_000
                };
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await admission.PrepareAsync(prepare, CancellationToken.None));
            }
        }

        internal async Task<NativeTransactionJournalSnapshot> ReadJournalSnapshotAsync()
        {
            HostManagerTransactionJournalAdmission? admission = null;
            var acquired = NativeTransactions.JournalState switch
            {
                HostManagerTransactionJournalRuntimeState.ReadyForExecution =>
                    NativeTransactions.TryAcquireExecutionAdmission(out admission),
                HostManagerTransactionJournalRuntimeState.RecoveryRequired =>
                    NativeTransactions.TryAcquireRecoveryAdmission(out admission),
                _ => false
            };
            Assert.True(acquired);
            Assert.NotNull(admission);
            await using (admission)
            {
                return await admission!.ReadSnapshotAsync(CancellationToken.None);
            }
        }

        internal void SimulateRecoveryRequiredJournalForValidationRead()
        {
            var runtime = typeof(HostManagerNativeActionTransactionRuntime)
                .GetField("journal", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(NativeTransactions)
                ?? throw new InvalidOperationException(
                    "The test transaction journal runtime is unavailable.");
            typeof(HostManagerTransactionJournalRuntime)
                .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(
                    runtime,
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired);
        }

        internal async Task<TimeSpan> RunRealtimeCycleAsync()
        {
            var method = typeof(HostManagerSmartCoordinator).GetMethod(
                "RunNativeCycleCoreAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(HostManagerSmartCoordinator).FullName,
                    "RunNativeCycleCoreAsync");
            var task = method.Invoke(
                Coordinator,
                ["scheduled", true, null, CancellationToken.None]) as Task<TimeSpan>
                ?? throw new InvalidOperationException(
                    "The realtime Host Manager cycle did not return its expected task.");
            try
            {
                return await task;
            }
            catch (InvalidDataException exception)
            {
                var snapshot = Workspace.Snapshot;
                var actions = string.Join(
                    "; ",
                    Enumerable.Range(0, checked((int)snapshot.ActionCount))
                        .Select(index =>
                        {
                            var action = Workspace.GetPlannedAction(checked((uint)index));
                            return $"id={action.ActionId},scope={action.Scope},disposition={action.Disposition},flags={action.Flags},valid={action.ValidMask},domains={action.DomainMask},cpuScore={action.CpuScore},gpuScore={action.GpuScore}";
                        }));
                throw new InvalidDataException(
                    $"{exception.Message} Snapshot: flags={snapshot.Flags}; reason={snapshot.ReasonMask}; config={snapshot.ConfigurationGeneration}; cycle={snapshot.CycleSequence}; plan={snapshot.PlanEpoch}; revision={snapshot.StateRevision}; observed={snapshot.ObservedAtMilliseconds}; wake={snapshot.WakeAfterMilliseconds}; next={snapshot.NextWakeAtMilliseconds}; actions={snapshot.ActionCount}; process={snapshot.ProcessCount}; software={snapshot.SoftwareCount}; inflight={snapshot.InflightCount}; rows={snapshot.SnapshotRowCount}; pending={snapshot.PendingCount}; invalid={snapshot.InvalidFactCount}; actionRows=[{actions}].",
                    exception);
            }
        }

        internal string DescribeMemoryCleanupState()
        {
            var authority = Coordinator.SchedulingAuthority;
            _ = ProcessFacts.Snapshot.TryGetCurrentDataset(
                SchedulingProcessMetricMask.CpuUsage,
                out var processCpu);
            _ = ProcessFacts.Snapshot.TryGetCurrentDataset(
                SchedulingProcessMetricMask.MemoryUsage,
                out var processMemory);
            _ = MetricSampler.Snapshot.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out var hardwareMemory);
            var baseline = ReadPrivateReference(
                Coordinator,
                "memoryCleanupHostedAdmissionBaseline");
            var baselineHardware = baseline?.GetType()
                .GetProperty("Hardware")?
                .GetValue(baseline) as HardwareMetricSnapshot;
            var baselineMemorySource = baselineHardware is not null
                && baselineHardware.TryGetCurrentDataset(
                    SamplingDatasetIds.SystemMemoryUsage,
                    out var baselineMemory)
                    ? baselineMemory.SourceGeneration
                    : 0;
            var dependencyCurrent = SystemMemoryUsageDependency.TryCreate(
                MetricSampler.Snapshot,
                DateTimeOffset.UtcNow,
                out var dependency);
            var finalFacts = dependencyCurrent
                ? SchedulingProcessFactTestProjection.Project(
                    ProcessFacts.Snapshot,
                    new SchedulingProcessFactRequest(
                        SchedulingProcessMetricMask.MemoryUsage
                            | SchedulingProcessMetricMask.RuntimeState,
                        dependency,
                        MetricSampler.Snapshot.GpuInventory))
                : null;
            return $"Memory cleanup planner calls={MemoryCleanupPlanner.PlanCalls}; authority={authority.Availability}; reason={authority.UnavailableReason ?? "none"}; schedulingGeneration={authority.Compute?.SchedulingGeneration ?? 0}; computeCpuSource={authority.Compute?.Cpu?.SourceGeneration ?? 0}; processCpuSource={processCpu?.SourceGeneration ?? 0}; processMemorySource={processMemory?.SourceGeneration ?? 0}; hardwareMemorySource={hardwareMemory?.SourceGeneration ?? 0}; baselineMemorySource={baselineMemorySource}; dependencyCurrent={dependencyCurrent}; finalFactsPresent={finalFacts is not null}; finalInventory={finalFacts?.IsInventoryCurrentComplete()}; finalMemory={finalFacts?.IsMemoryCurrentComplete(dependency!)}; finalRuntime={finalFacts?.IsRuntimeStateCurrentComplete()}; finalGeneration={finalFacts?.Generation ?? 0}; finalObserved={finalFacts?.ObservedAtUtcTicks ?? 0}.";
        }

        internal void PublishNextHostedCpuObservation()
        {
            MetricSampler.PublishNextHostedCpuObservation();
            ProcessFacts.PublishNextHostedCpuObservation();
        }

        internal async Task PrimeMemoryCleanupAdmissionAsync(
            bool publishHardware = true,
            bool publishProcessMemory = true)
        {
            var baseline = MetricSampler.Snapshot;
            _ = await RunRealtimeCycleAsync();
            if (publishHardware
                && !HasNewerMemoryDataset(baseline, MetricSampler.Snapshot))
            {
                MetricSampler.PublishNextHostedMemoryObservation();
            }
            if (publishProcessMemory
                && SystemMemoryUsageDependency.TryCreate(
                    MetricSampler.Snapshot,
                    MetricSampler.Snapshot.CapturedAt,
                    out _))
            {
                ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
                    ProcessFacts.Snapshot,
                    MetricSampler.Snapshot));
            }
        }

        private static bool HasNewerMemoryDataset(
            HardwareMetricSnapshot baseline,
            HardwareMetricSnapshot current)
            => baseline.TryGetCurrentDataset(
                    SamplingDatasetIds.SystemMemoryUsage,
                    out var baselineMemory)
                && current.TryGetCurrentDataset(
                    SamplingDatasetIds.SystemMemoryUsage,
                    out var currentMemory)
                && currentMemory.WorkspaceIdentity == baselineMemory.WorkspaceIdentity
                && currentMemory.ConfigurationGeneration ==
                    baselineMemory.ConfigurationGeneration
                && currentMemory.CatalogGeneration == baselineMemory.CatalogGeneration
                && currentMemory.SourceGeneration > baselineMemory.SourceGeneration
                && currentMemory.CommittedGeneration >
                    baselineMemory.CommittedGeneration
                && currentMemory.ObservedAtUtcTicks >
                    baselineMemory.ObservedAtUtcTicks;

        internal void CloseMemoryCleanupJournalForRestart()
            => memoryCleanupJournal.Dispose();

        internal void AssertNoMemoryCleanupAttemptEntries()
            => Assert.Empty(memoryCleanupJournal.ReconcileAndCaptureBlocked(
                ProcessPolicyWriter.ReadProcessInstanceForRecovery));

        internal void AssertNoEffectCollaboratorCalls()
        {
            CaptureHostedScoreOnlyEffectCounters().AssertAllZero();
        }

        internal HostedScoreOnlyEffectCounterSnapshot CaptureHostedScoreOnlyEffectCounters()
            => new(
                StateStore.ReserveAttemptCalls,
                StateStore.ReserveCalls,
                StateStore.SaveAttemptCalls,
                StateStore.SaveCalls,
                ProcessPolicyWriter.TotalCalls,
                ProcessPolicyWriter.RecoveryReadCalls,
                ProcessPolicyWriter.BatchCalls,
                ProcessPolicyWriter.BatchRequests.Count,
                AdapterDispatcher.TotalCalls,
                MemoryCleanupPlanner.PlanAttemptCalls,
                MemoryCleanupPlanner.PlanCalls,
                MemoryCleanupPlanner.CompleteAttemptCalls,
                MemoryCleanupPlanner.CompleteCalls,
                MemoryCleanupPlanner.CompletionHistory.Count,
                PublicResourceManager.TickAttemptCalls,
                PublicResourceManager.TickCalls,
                PublicResourceManager.NewEffectAttemptCount,
                GraphicsPreferenceStore.TotalCalls);

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            ownedProcessFactsOverride?.Dispose();
            memoryCleanupJournal.Dispose();
            MemoryCleanupPlanner.Dispose();
            await NativeTransactions.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingRollbackStateStore(
        HostManagerRollbackStateDocument current) : IHostManagerRollbackStateStore
    {
        internal HostManagerRollbackStateDocument Current { get; set; } = current;
        internal int LoadCalls { get; private set; }
        internal int ReserveCalls { get; private set; }
        internal int SaveCalls { get; private set; }
        internal int ReserveAttemptCalls { get; private set; }
        internal int SaveAttemptCalls { get; private set; }
        internal Action? OnReserve { get; set; }
        internal bool FailOnEffectCall { get; init; }

        public Task<HostManagerRollbackStateDocument> LoadAsync(
            CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Task.FromResult(Current);
        }

        public Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(
            CancellationToken cancellationToken)
        {
            ReserveAttemptCalls++;
            if (FailOnEffectCall)
            {
                throw new InvalidOperationException(
                    "Score-only called the rollback-state reservation effect boundary.");
            }
            ReserveCalls++;
            OnReserve?.Invoke();
            var nextIncarnation = checked(Current.NativeHostSessionIncarnation + 1);
            Current = Current with
            {
                NativeHostSessionIncarnation = nextIncarnation,
                Message = $"Host Manager reserved native host session incarnation {nextIncarnation}."
            };
            return Task.FromResult(Current);
        }

        public Task SaveAsync(
            HostManagerRollbackStateDocument document,
            CancellationToken cancellationToken)
        {
            SaveAttemptCalls++;
            if (FailOnEffectCall)
            {
                throw new InvalidOperationException(
                    "Score-only called the rollback-state save effect boundary.");
            }
            if (document.NativeHostSessionIncarnation
                != Current.NativeHostSessionIncarnation)
            {
                throw new InvalidOperationException(
                    "Native Host session incarnation can only change through an atomic reservation.");
            }

            SaveCalls++;
            Current = document;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMetricSampler :
        IMetricSampler,
        IMetricSnapshotObservationSource
    {
        private readonly LastSuccessfulHardwareMetricSnapshot historyOwner = new();
        private HardwareMetricSnapshot _snapshot = null!;
        private int activeSubscriptionCount;

        internal RecordingMetricSampler(HardwareMetricSnapshot snapshot, CompiledDataHistoryPlan history)
        {
            historyOwner.ConfigureHistory(history);
            SetSnapshot(snapshot);
        }

        internal int CaptureCalls { get; private set; }
        internal int ReadLatestCalls { get; private set; }
        internal int ActiveSubscriptionCount => Volatile.Read(ref activeSubscriptionCount);
        internal bool AdvanceMemoryOnlyCapture { get; set; } = true;
        internal bool ObservationAvailable { get; set; } = true;
        internal Action? MemoryOnlyCaptureHandler { get; set; }
        internal MetricSampleRequest? LastCaptureRequest { get; private set; }
        internal List<MetricSampleRequest> CaptureRequests { get; } = [];
        internal List<MetricObservationSubscription> SubscriptionHistory { get; } = [];
        internal HardwareMetricSnapshot Snapshot => _snapshot;
        internal void SetSnapshot(HardwareMetricSnapshot value)
        {
            historyOwner.Publish(value, MetricSampleRequest.ForIds(value.Datasets.Keys));
            _snapshot = value with { History = historyOwner.Read()!.History };
        }

        internal void SetCpuUsagePercent(double value)
        {
            _snapshot = _snapshot with
            {
                Cpu = _snapshot.Cpu with { UsagePercent = value }
            };
            PublishHistory(SamplingDatasetIds.SystemCpuUsage);
        }

        private void PublishHistory(string datasetId)
        {
            historyOwner.Publish(_snapshot, MetricSampleRequest.ForIds([datasetId]));
            _snapshot = _snapshot with { History = historyOwner.Read()!.History };
        }

        internal void PublishNextHostedCpuObservation()
            => PublishNextHostedObservation(SamplingDatasetIds.SystemCpuUsage);

        internal void PublishNextHostedMemoryObservation()
            => PublishNextHostedObservation(SamplingDatasetIds.SystemMemoryUsage);

        private void PublishNextHostedObservation(string datasetId)
        {
            if (!_snapshot.Datasets.TryGetValue(datasetId, out var current))
            {
                throw new InvalidOperationException(
                    $"The test hardware snapshot has no {datasetId} dataset.");
            }
            var observedAt = _snapshot.CapturedAt.AddTicks(1);
            var sourceGeneration = checked(current.SourceGeneration + 1);
            var committedGeneration = checked(current.CommittedGeneration + 1);
            var datasets = _snapshot.Datasets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            datasets[datasetId] = current with
            {
                SourceGeneration = sourceGeneration,
                ObservedAtUtcTicks = observedAt.UtcTicks,
                CommittedGeneration = committedGeneration,
                LastAttemptAtUtcTicks = observedAt.UtcTicks,
                LastSuccessAtUtcTicks = current.Status ==
                    SamplingObservationStatus.Current
                        ? observedAt.UtcTicks
                        : current.LastSuccessAtUtcTicks,
                ReadyUntilUtcTicks = DateTimeOffset.MaxValue.UtcTicks
            };
            _snapshot = _snapshot with
            {
                CapturedAt = observedAt,
                Cpu = string.Equals(
                        datasetId,
                        SamplingDatasetIds.SystemCpuUsage,
                        StringComparison.OrdinalIgnoreCase)
                    ? _snapshot.Cpu with { SourceGeneration = sourceGeneration }
                    : _snapshot.Cpu,
                CommittedGeneration = Math.Max(
                    _snapshot.CommittedGeneration,
                    committedGeneration),
                Datasets = datasets
            };
            PublishHistory(datasetId);
        }

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(_snapshot);

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(_snapshot);

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
        {
            CaptureCalls++;
            LastCaptureRequest = request;
            CaptureRequests.Add(request);
            var isMemoryOnly = request.Includes("memory.usage")
                && !request.Includes("cpu.usage");
            if (isMemoryOnly)
            {
                MemoryOnlyCaptureHandler?.Invoke();
            }
            return Task.FromResult(
                AdvanceMemoryOnlyCapture
                    && isMemoryOnly
                    ? _snapshot with
                    {
                        CapturedAt = _snapshot.CapturedAt.AddTicks(1),
                        CommittedGeneration = checked(_snapshot.CommittedGeneration + 1)
                    }
                    : _snapshot);
        }

        public IDisposable AcquireSubscription(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
            SubscriptionHistory.Add(new MetricObservationSubscription(
                subscriptionId,
                request,
                refreshInterval));
            Interlocked.Increment(ref activeSubscriptionCount);
            return new RecordingSubscription(
                () => Interlocked.Decrement(ref activeSubscriptionCount));
        }

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            ReadLatestCalls++;
            return ObservationAvailable ? _snapshot : null;
        }
    }

    private sealed class HostedHardwareObservationCache
    {
        private readonly LastSuccessfulHardwareMetricSnapshot owner = new();
        private bool configured;

        internal void Configure(CompiledDataHistoryPlan history)
        {
            ArgumentNullException.ThrowIfNull(history);
            if (configured)
            {
                throw new InvalidOperationException(
                    "The hosted hardware observation cache can only be configured once.");
            }

            owner.ConfigureHistory(history);
            configured = true;
        }

        internal HardwareMetricSnapshot Publish(
            HardwareMetricSnapshot snapshot,
            MetricSampleRequest request)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(request);
            if (!configured)
            {
                throw new InvalidOperationException(
                    "The hosted hardware observation cache is not configured.");
            }

            var publication = owner.Publish(snapshot, request);
            if (publication.RequestedDatasetCount == 0)
            {
                throw new InvalidDataException(
                    "The hosted hardware observation cache received an empty request.");
            }

            var current = owner.Read()
                ?? throw new InvalidDataException(
                    "The hosted hardware observation cache did not publish a current value.");
            return current;
        }
    }

    private sealed class RecordingProcessFactSource(SchedulingProcessFactSnapshot snapshot)
        : ISchedulingProcessFactSource,
          ISchedulingProcessFactObservationSource
    {
        private int activeSubscriptionCount;

        internal int CaptureCalls { get; private set; }
        internal int ReadLatestCalls { get; private set; }
        internal int ActiveSubscriptionCount => Volatile.Read(ref activeSubscriptionCount);
        internal bool AdvanceMemoryOnlyCapture { get; set; } = true;
        internal List<ProcessObservationSubscription> SubscriptionHistory { get; } = [];
        internal List<SchedulingProcessFactRequest> ReadLatestRequests { get; } = [];
        internal Func<
            SchedulingProcessFactRequest,
            SchedulingProcessFactSnapshot?>? ReadLatestHandler { get; set; }
        internal SchedulingProcessFactSnapshot Snapshot => snapshot;
        internal void SetSnapshot(SchedulingProcessFactSnapshot value) => snapshot = value;

        internal void PublishNextHostedCpuObservation()
        {
            if (!snapshot.DatasetObservations.TryGetValue(
                    SchedulingProcessMetricMask.CpuUsage,
                    out var current))
            {
                throw new InvalidOperationException(
                    "The test process snapshot has no process-CPU dataset.");
            }

            var observedAtUtcTicks = checked(current.ObservedAtUtcTicks + 1);
            var observations = snapshot.DatasetObservations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value);
            observations[SchedulingProcessMetricMask.CpuUsage] = current with
            {
                SourceGeneration = checked(current.SourceGeneration + 1),
                ObservedAtUtcTicks = observedAtUtcTicks,
                LastAttemptAtUtcTicks = observedAtUtcTicks,
                LastSuccessAtUtcTicks = observedAtUtcTicks,
                ReadyUntilUtcTicks = DateTimeOffset.MaxValue.UtcTicks
            };
            snapshot = snapshot with { DatasetObservations = observations };
        }

        public Task<SchedulingProcessFactSnapshot> CaptureAsync(
            SchedulingProcessFactRequest request,
            CancellationToken cancellationToken)
        {
            CaptureCalls++;
            if (!AdvanceMemoryOnlyCapture
                || request.RequestedMetricMask != SchedulingProcessMetricMask.MemoryUsage)
            {
                return Task.FromResult(snapshot);
            }

            var generation = checked(snapshot.Generation + 1);
            return Task.FromResult(snapshot with
            {
                Generation = generation,
                ObservedAtUtcTicks = checked(snapshot.ObservedAtUtcTicks + 1),
                RequestedMetricMask = request.RequestedMetricMask,
                CurrentMetricMask = request.RequestedMetricMask,
                Processes = snapshot.Processes
                    .Select(process => process with { SourceGeneration = generation })
                    .ToArray()
            });
        }

        public IDisposable AcquireSubscription(
            string subscriptionId,
            SchedulingProcessMetricMask metricMask,
            TimeSpan refreshInterval)
        {
            SubscriptionHistory.Add(new ProcessObservationSubscription(
                subscriptionId,
                metricMask,
                refreshInterval));
            Interlocked.Increment(ref activeSubscriptionCount);
            return new RecordingSubscription(
                () => Interlocked.Decrement(ref activeSubscriptionCount));
        }

        public SchedulingProcessFactSnapshot? ReadLatest(
            SchedulingProcessFactRequest request)
        {
            ReadLatestCalls++;
            ReadLatestRequests.Add(request);
            return ReadLatestHandler?.Invoke(request)
                ?? SchedulingProcessFactTestProjection.Project(snapshot, request);
        }
    }

    private sealed record MetricObservationSubscription(
        string SubscriptionId,
        MetricSampleRequest Request,
        TimeSpan RefreshInterval);

    private sealed record ProcessObservationSubscription(
        string SubscriptionId,
        SchedulingProcessMetricMask MetricMask,
        TimeSpan RefreshInterval);

    private sealed class RecordingSubscription(Action onDispose) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }

    private sealed class AllowingControlZones : IHostManagerSmartControlZoneRegistry
    {
        internal AllowingControlZones(bool policyExecutionEnabled = true)
        {
            PolicyExecutionEnabled = policyExecutionEnabled;
        }

        internal bool PolicyExecutionEnabled { get; set; }

        public bool CanRun(string zoneId)
            => PolicyExecutionEnabled || !zoneId.Equals(
                HostManagerSmartControlZoneIds.PolicyExecution,
                StringComparison.Ordinal);
        public bool IsLowPower(string zoneId) => false;
        public bool HasAnyLowPowerZone() => false;
        public ResourceManagerComputeZoneMode GetMode(string zoneId)
            => ResourceManagerComputeZoneMode.Normal;
        public IReadOnlyList<IResourceManagerSelfComputeZone> GetZones() => [];
    }

    private sealed class RecordingAutomaticMemoryCleanupPlanner(
        ulong configurationGeneration)
        : IAutomaticMemoryCleanupPlanner
    {
        internal int PlanCalls { get; private set; }
        internal int CompleteCalls { get; private set; }
        internal int PlanAttemptCalls { get; private set; }
        internal int CompleteAttemptCalls { get; private set; }
        internal AutomaticMemoryCleanupPlanRequest? LastRequest { get; private set; }
        internal AutomaticMemoryCleanupPlanResult PlanResult { get; set; } =
            AutomaticMemoryCleanupPlanResult.Empty;
        internal Func<
            AutomaticMemoryCleanupPlanRequest,
            AutomaticMemoryCleanupPlanResult>? PlanHandler
        { get; set; }
        internal IReadOnlyList<AutomaticMemoryCleanupFeedback> LastFeedback { get; private set; } = [];
        internal List<IReadOnlyList<AutomaticMemoryCleanupFeedback>> CompletionHistory { get; } = [];
        internal bool PreserveZeroConfigurationGeneration { get; set; }
        internal bool FailOnCall { get; init; }

        public AutomaticMemoryCleanupPlanResult Plan(
            AutomaticMemoryCleanupPlanRequest request)
        {
            PlanAttemptCalls++;
            if (FailOnCall)
            {
                throw new InvalidOperationException(
                    "Score-only called the automatic memory-cleanup planning effect boundary.");
            }
            PlanCalls++;
            LastRequest = request;
            var result = PlanHandler?.Invoke(request) ?? PlanResult;
            return result.ConfigurationGeneration == 0
                && !PreserveZeroConfigurationGeneration
                ? result with { ConfigurationGeneration = configurationGeneration }
                : result;
        }

        public void Complete(IReadOnlyList<AutomaticMemoryCleanupFeedback> feedback)
        {
            CompleteAttemptCalls++;
            if (FailOnCall)
            {
                throw new InvalidOperationException(
                    "Score-only called the automatic memory-cleanup completion effect boundary.");
            }
            CompleteCalls++;
            LastFeedback = feedback.ToArray();
            CompletionHistory.Add(LastFeedback);
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingPublicResourceManager :
        IHostPublicResourceSelfManager, IHostPublicResourceCapability,
        IHostPublicResourcePublicationNotifier
    {
        private bool available = true;
        internal bool Available
        {
            get => available;
            set
            {
                available = value;
                if (value) HostResourcePublished?.Invoke();
            }
        }
        public event Action? HostResourcePublished;
        internal int TickCalls { get; private set; }
        internal int TickAttemptCalls { get; private set; }
        internal int PendingNewEffectCandidates { get; set; }
        internal uint NewEffectAttemptCount { get; private set; }
        internal HostPublicResourceSelfManagerTickRequest? LastRequest { get; private set; }
        internal Action? TickHandler { get; set; }
        internal bool FailOnCall { get; init; }

        public HostPublicResourceCapabilitySnapshot GetCapability()
            => new(
                Available
                    ? HostPublicResourceCapabilityStates.Available
                    : HostPublicResourceCapabilityStates.Unavailable,
                Available,
                Available ? "test-available" : "test-unavailable");

        public HostPublicResourceSelfManagerTickResult Tick(
            HostPublicResourceSelfManagerTickRequest request)
        {
            TickAttemptCalls++;
            if (FailOnCall)
            {
                throw new InvalidOperationException(
                    "Score-only called the public-resource effect boundary.");
            }
            TickCalls++;
            LastRequest = request;
            TickHandler?.Invoke();
            var attemptCount = Math.Min(
                checked((uint)PendingNewEffectCandidates),
                request.MaximumNewEffectAttempts);
            PendingNewEffectCandidates = checked(
                PendingNewEffectCandidates - (int)attemptCount);
            NewEffectAttemptCount = checked(NewEffectAttemptCount + attemptCount);
            return new HostPublicResourceSelfManagerTickResult(
                PlannedCount: checked((int)attemptCount),
                UnloadedCount: checked((int)attemptCount),
                RecalledCount: 0,
                RejectedCount: 0,
                NewEffectAttemptCount: attemptCount,
                RecoveryAttemptCount: 0,
                ReleasedBytes: attemptCount,
                RestoredBytes: 0,
                SampleGeneration: checked((ulong)TickCalls));
        }
    }

    private sealed class RecordingAdapterPolicyDispatcher : IAdapterPolicyDispatcher
    {
        internal int TotalCalls { get; private set; }

        public Task<AdapterSoftwareSchedulingResult> ApplySoftwareSchedulingAsync(
            string softwareId,
            AdapterSoftwareSchedulingEnvelope envelope,
            CancellationToken cancellationToken)
            => throw Unexpected();

        public Task<AdapterSchedulingStateExportResult> ExportSoftwareSchedulingStateAsync(
            string softwareId,
            int maximumPayloadBytes,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
            => throw Unexpected();

        public Task<AdapterSchedulingStateRestoreResult> RestoreSoftwareSchedulingStateAsync(
            string softwareId,
            AdapterSchedulingStateRestoreCommand command,
            CancellationToken cancellationToken)
            => throw Unexpected();

        private InvalidOperationException Unexpected()
        {
            TotalCalls++;
            return new InvalidOperationException("Score-only called an adapter effect boundary.");
        }
    }

    private sealed class RecordingGraphicsPreferenceStore : IWindowsGraphicsPreferenceStore
    {
        internal int TotalCalls { get; private set; }

        public RecoveryReadResult<string> ReadValueForRecovery(string executablePath)
            => throw Unexpected();
        public void WriteValue(string executablePath, string value) => throw Unexpected();
        public void DeleteValue(string executablePath) => throw Unexpected();
        public string BuildPreferIntegratedGpuValue(string? currentValue) => throw Unexpected();
        public string BuildPreferHighPerformanceGpuValue(string? currentValue) => throw Unexpected();
        public string DescribePreference(string? value) => throw Unexpected();

        private InvalidOperationException Unexpected()
        {
            TotalCalls++;
            return new InvalidOperationException("Score-only called a graphics restore boundary.");
        }
    }

    private sealed class RecordingProcessPolicyWriter(
        Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>>? processRecoveryRead = null)
        : IProcessResourcePolicyWriter
    {
        internal int TotalCalls { get; private set; }
        internal int RecoveryReadCalls { get; private set; }
        internal int PlacementReadCalls { get; private set; }
        internal int BatchCalls { get; private set; }
        internal int DefaultCpuSetWriteCalls { get; private set; }
        internal IReadOnlyList<ProcessResourcePolicyBatchRequest> BatchRequests { get; private set; } = [];
        internal Func<
            IReadOnlyList<ProcessResourcePolicyBatchRequest>,
            IReadOnlyList<ProcessResourcePolicyBatchWriteResult>>? BatchHandler
        { get; set; }
        internal Func<int, ProcessResourcePolicySnapshot?>? ProcessReadHandler { get; set; }
        internal Func<
            int,
            ProcessPlacementReadFields,
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>>? PlacementReadHandler
        { get; set; }
        internal Func<int, IReadOnlyList<uint>, ProcessResourcePolicyWriteResult>?
            DefaultCpuSetWriteHandler
        { get; set; }
        internal Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>>?
            RecoveryReadHandler { get; set; } = processRecoveryRead;

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
        {
            if (ProcessReadHandler is null)
            {
                throw Unexpected();
            }

            TotalCalls++;
            return ProcessReadHandler(processId);
        }
        public DateTimeOffset? TryReadProcessStartedAt(int processId) => throw Unexpected();
        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
            int processId)
        {
            if (RecoveryReadHandler is null)
            {
                throw Unexpected();
            }

            TotalCalls++;
            RecoveryReadCalls++;
            return RecoveryReadHandler(processId);
        }
        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int processId,
            ProcessPlacementReadFields requiredFields)
        {
            if (PlacementReadHandler is null)
            {
                throw Unexpected();
            }

            TotalCalls++;
            PlacementReadCalls++;
            return PlacementReadHandler(processId, requiredFields);
        }
        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
            int processId,
            int threadId) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetPriorityClass(
            int processId,
            string priorityClass) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
            int processId,
            long affinityMask) => throw Unexpected();
        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId)
            => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds)
        {
            if (DefaultCpuSetWriteHandler is null)
            {
                throw Unexpected();
            }

            TotalCalls++;
            DefaultCpuSetWriteCalls++;
            return DefaultCpuSetWriteHandler(processId, cpuSetIds);
        }
        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
            int processId,
            int threadId) => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds) => throw Unexpected();
        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId)
            => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority) => throw Unexpected();
        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId)
            => throw Unexpected();
        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask) => throw Unexpected();
        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            if (BatchHandler is null)
            {
                throw Unexpected();
            }

            TotalCalls++;
            BatchCalls++;
            BatchRequests = requests.ToArray();
            return BatchHandler(requests);
        }

        private InvalidOperationException Unexpected()
        {
            TotalCalls++;
            return new InvalidOperationException("Score-only called a process policy boundary.");
        }
    }

    private sealed class ExactJobMembershipProbe : IWindowsJobMembershipProbe
    {
        public WindowsJobMembershipResult Probe(
            string jobName,
            HostManagerComputeProcessIdentity identity)
            => new(WindowsJobMembershipStatus.ExactMember, 0);
    }

    private sealed class CoordinatorValidationScopeFixture : IDisposable
    {
        private CoordinatorValidationScopeFixture(
            string root,
            FailingValidationScopeCommitter committer,
            HostManagerProcessEffectValidationScopeAuthority authority,
            Guid scopeId,
            Guid runNonce,
            string releaseToken)
        {
            Root = root;
            Committer = committer;
            Authority = authority;
            ScopeId = scopeId;
            RunNonce = runNonce;
            ReleaseToken = releaseToken;
        }

        internal string Root { get; }
        internal FailingValidationScopeCommitter Committer { get; }
        internal HostManagerProcessEffectValidationScopeAuthority Authority { get; }
        internal Guid ScopeId { get; }
        internal Guid RunNonce { get; }
        internal string ReleaseToken { get; }

        internal HostManagerProcessEffectValidationScopeCloseRequest CreateCloseRequest()
            => new(ScopeId, RunNonce, ReleaseToken);

        internal static CoordinatorValidationScopeFixture Create(
            HostManagerComputeProcessIdentity identity)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "resource-manager-score-only-validation",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var committer = new FailingValidationScopeCommitter();
            var authority = new HostManagerProcessEffectValidationScopeAuthority(
                Path.Combine(root, "validation-scope", "scope.json"),
                TimeProvider.System,
                new ExactJobMembershipProbe(),
                committer);
            var runNonce = Guid.NewGuid();
            var releaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var opened = authority.Open(new(
                runNonce,
                $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow.AddMinutes(10),
                releaseToken,
                [
                    new HostManagerProcessEffectValidationIdentity(
                        identity.ProcessId,
                        checked((long)identity.ProcessStartKey))
                ],
                AllowAutomaticMemoryCleanup: false,
                AllowNonAdaptedMemoryTransaction: true));
            return new(
                root,
                committer,
                authority,
                opened.ScopeId!.Value,
                runNonce,
                releaseToken);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FailingValidationScopeCommitter
        : IHostManagerProcessEffectValidationScopeFileCommitter
    {
        internal int CommitCallCount { get; private set; }
        internal int? FailCommitCallNumber { get; set; }

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            bool replaceExisting)
        {
            CommitCallCount++;
            if (FailCommitCallNumber == CommitCallCount)
            {
                FailCommitCallNumber = null;
                throw new IOException("injected validation-scope commit failure");
            }
            File.Move(temporaryPath, canonicalPath, overwrite: replaceExisting);
        }

        public void DeleteExact(string canonicalPath)
        {
            try
            {
                File.Delete(canonicalPath);
            }
            catch (FileNotFoundException)
            {
            }
        }
    }

    private sealed class RecordingProtectionService : IOptimizationProtectionService
    {
        internal IReadOnlyList<ProtectedOptimizationTarget> Targets { get; set; } = [];
        internal int GetCalls { get; private set; }

        public int ProtectedTargetCount => Targets.Count;
        public Task<ProtectedOptimizationTarget?> ProtectReportAsync(
            OptimizationReportItem report,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(
            CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(Targets);
        }
        public Task<IReadOnlyList<ProtectedOptimizationTarget>> RefreshProtectedTargetsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RemoveProtectedTargetAsync(
            string protectionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
            string protectionId,
            int protectionLevel,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsTargetProtectedAsync(
            OptimizationReportTarget target,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StaticCpuTopologyReader : ICpuTopologySampler
    {
        public CpuTopologySnapshot CaptureSnapshot()
            => throw new InvalidOperationException("Runtime plan compilation must not sample CPU usage.");

        public CpuTopologySnapshot CaptureTopology()
            => new(
                DateTimeOffset.UtcNow,
                "test",
                new CpuSpecificationModel("test", "test", "test", 0, 0, null, null, null, null, "test"),
                "test",
                "test",
                CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
                CpuTopologyVisualLayoutKinds.Grid,
                "test",
                0,
                0,
                0,
                false,
                [],
                [],
                [],
                []);
    }

    private sealed class StaticCpuResidencyReader : ICpuCoreResidencyReader
    {
        public CpuCoreResidencySnapshot? Read() => null;
        public IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval)
            => new NoopDisposable();
        public IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
            string subscriptionId,
            TimeSpan interval,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("This score-only fixture has no frontend subscriptions.");
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private interface IScoreOnlyDiagnosticRecordObserver
    {
        void Observe(DebugDiagnosticLogRecord record);
    }

    private sealed class RecordingDebugLogWriter(
        IDebugDiagnosticLogWriter? initialForwardingWriter = null)
        : IDebugDiagnosticLogWriter
    {
        private IDebugDiagnosticLogWriter? forwardingWriter = initialForwardingWriter;
        private IScoreOnlyDiagnosticRecordObserver? recordObserver;
        private int retainRecords = 1;
        internal List<DebugDiagnosticLogRecord> Records { get; } = [];

        internal bool ForwardingWriterDetached
            => Volatile.Read(ref forwardingWriter) is null;

        internal void PrepareRecordCapacity(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            if (Volatile.Read(ref retainRecords) == 0
                || Records.Count != 0
                || Volatile.Read(ref forwardingWriter) is not null)
            {
                throw new InvalidOperationException(
                    "The recording debug writer must be idle before reserving capacity.");
            }
            Records.Capacity = capacity;
        }

        internal void DisableRecordRetention()
        {
            if (Records.Count != 0
                || Volatile.Read(ref recordObserver) is not null
                || Volatile.Read(ref forwardingWriter) is not null
                || Interlocked.CompareExchange(ref retainRecords, 0, 1) != 1)
            {
                throw new InvalidOperationException(
                    "Record retention can only be disabled once before recording starts.");
            }
        }

        internal void AttachForwardingWriter(IDebugDiagnosticLogWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            if (Interlocked.CompareExchange(ref forwardingWriter, writer, null) is not null)
            {
                throw new InvalidOperationException(
                    "A forwarding debug diagnostic log writer is already attached.");
            }
        }

        internal void AttachRecordObserver(IScoreOnlyDiagnosticRecordObserver observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            if (Records.Count != 0 ||
                Interlocked.CompareExchange(ref recordObserver, observer, null) is not null)
            {
                throw new InvalidOperationException(
                    "A debug diagnostic record observer is already attached or recording has started.");
            }
        }

        internal void DetachRecordObserver(IScoreOnlyDiagnosticRecordObserver observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            if (!ReferenceEquals(
                    Interlocked.CompareExchange(ref recordObserver, null, observer),
                    observer))
            {
                throw new InvalidOperationException(
                    "The expected debug diagnostic record observer is not attached.");
            }
        }

        internal void DetachForwardingWriter(IDebugDiagnosticLogWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            if (!ReferenceEquals(
                    Interlocked.CompareExchange(ref forwardingWriter, null, writer),
                    writer))
            {
                throw new InvalidOperationException(
                    "The expected forwarding debug diagnostic log writer is not attached.");
            }
        }

        public bool TryWrite(DebugDiagnosticLogRecord record)
        {
            if (Volatile.Read(ref retainRecords) != 0)
            {
                Records.Add(record);
            }
            Volatile.Read(ref recordObserver)?.Observe(record);
            return Volatile.Read(ref forwardingWriter)?.TryWrite(record) ?? true;
        }
    }

    private sealed class UnavailableMemoryModePolicySource : IHostManagerMemoryModePolicySource
    {
        public HostManagerMemoryModePolicyCapture Capture(
            HostManagerSchedulingPlanBinding planBinding,
            CompiledHostManagerSmartCoordinatorPlan smartCoordinatorPlan)
            => HostManagerMemoryModePolicyCapture.Unavailable("score-only-test");
    }

    private static HardwareMetricSnapshot CreateHardwareSnapshot(
        double memoryUsagePercent = 99,
        double cpuUsagePercent = 50,
        bool memoryUsageAvailable = true,
        ulong committedGeneration = 1,
        SamplingObservationStatus? memoryObservationStatus = null,
        DateTimeOffset? capturedAt = null,
        bool gpuInventoryCurrent = false,
        double virtualMemoryUsagePercent = 50,
        bool virtualMemoryUsageAvailable = true,
        SamplingObservationStatus? virtualMemoryObservationStatus = null)
    {
        var now = capturedAt ?? DateTimeOffset.UtcNow;
        var memoryStatus = memoryObservationStatus
            ?? (memoryUsageAvailable
                ? SamplingObservationStatus.Current
                : SamplingObservationStatus.Unavailable);
        var virtualMemoryStatus = virtualMemoryObservationStatus
            ?? (virtualMemoryUsageAvailable
                ? SamplingObservationStatus.Current
                : SamplingObservationStatus.Unavailable);
        return new HardwareMetricSnapshot(
            now,
            new CpuMetrics(
                "test",
                cpuUsagePercent,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                100,
                1,
                1,
                100,
                "test",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("test", "Unavailable", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(
                checked((ulong)memoryUsagePercent),
                100,
                memoryUsagePercent,
                memoryUsageAvailable,
                "test")
            {
                ObservationStatus = memoryStatus
            },
            new VirtualMemoryMetrics(
                checked((ulong)virtualMemoryUsagePercent),
                100,
                virtualMemoryUsagePercent,
                "test",
                virtualMemoryUsageAvailable)
            {
                ObservationStatus = virtualMemoryStatus
            },
            [],
            new SchedulingGpuInventorySnapshot(
                gpuInventoryCurrent
                    ? SamplingObservationStatus.Current
                    : SamplingObservationStatus.NotRequested,
                gpuInventoryCurrent ? 1UL : 0UL,
                gpuInventoryCurrent ? now.UtcTicks : 0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricValue>())
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemCpuUsage] =
                    CreateHardwareDatasetObservation(
                        SamplingDatasetIds.SystemCpuUsage,
                        SamplingObservationStatus.Current,
                        now,
                        committedGeneration),
                [SamplingDatasetIds.SystemMemoryUsage] =
                    CreateHardwareDatasetObservation(
                        SamplingDatasetIds.SystemMemoryUsage,
                        memoryStatus,
                        now,
                        committedGeneration),
                [SamplingDatasetIds.SystemVirtualMemoryUsage] =
                    CreateHardwareDatasetObservation(
                        SamplingDatasetIds.SystemVirtualMemoryUsage,
                        virtualMemoryStatus,
                        now,
                        committedGeneration)
            },
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = committedGeneration
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan duration) => current += duration;
    }

    private static SchedulingProcessFactSnapshot CreateProcessFacts()
    {
        const SchedulingProcessMetricMask metrics =
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState;
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            1,
            observedAt,
            0,
            0,
            0,
            0,
            metrics,
            metrics,
            [])
        {
            DatasetObservations = CreateCurrentDatasetObservations(
                metrics,
                1,
                observedAt)
        };
    }

    private static SchedulingProcessFactSnapshot CreateProcessFacts(
        int processId,
        ulong processStartKey,
        string softwareId)
    {
        var process = new SchedulingProcessFact(
            processId,
            processStartKey,
            "pid-reuse",
            @"c:\tests\pid-reuse.exe",
            softwareId,
            "PID reuse",
            "general",
            "General",
            100,
            SchedulingProcessMetricMask.RuntimeState,
            0,
            0,
            1,
            []);
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        const SchedulingProcessMetricMask metrics =
            SchedulingProcessMetricMask.RuntimeState;
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            1,
            observedAt,
            1,
            1,
            0,
            0,
            metrics,
            metrics,
            [process])
        {
            DatasetObservations = CreateCurrentDatasetObservations(
                metrics,
                1,
                observedAt)
        };
    }

    private static SchedulingProcessFactSnapshot CreateCompleteProcessFacts(
        int processId,
        ulong processStartKey,
        string softwareId,
        double baseScore,
        double cpuUsagePercent,
        double memoryUsagePercent)
        => CreateCompleteProcessFacts(
            (
                processId,
                processStartKey,
                softwareId,
                baseScore,
                cpuUsagePercent,
                memoryUsagePercent));

    private static SchedulingProcessFactSnapshot CreateCompleteProcessFacts(
        params (
            int ProcessId,
            ulong ProcessStartKey,
            string SoftwareId,
            double BaseScore,
            double CpuUsagePercent,
            double MemoryUsagePercent)[] inputs)
    {
        const SchedulingProcessMetricMask metrics =
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState;
        var processes = inputs
            .Select(static input => new SchedulingProcessFact(
                input.ProcessId,
                input.ProcessStartKey,
                $"cycle-budget-{input.ProcessId}",
                $@"c:\tests\cycle-budget-{input.ProcessId}.exe",
                input.SoftwareId,
                $"Cycle budget {input.ProcessId}",
                "general",
                "General",
                input.BaseScore,
                metrics,
                input.CpuUsagePercent,
                input.MemoryUsagePercent,
                SourceGeneration: 1,
                Gpus: []))
            .ToArray();
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            Generation: 1,
            ObservedAtUtcTicks: observedAt,
            EnumeratedCount: checked((uint)processes.Length),
            EmittedCount: checked((uint)processes.Length),
            SkippedCount: 0,
            OverflowCount: 0,
            RequestedMetricMask: metrics,
            CurrentMetricMask: metrics,
            Processes: processes)
        {
            DatasetObservations = CreateCurrentDatasetObservations(
                metrics,
                1,
                observedAt)
        };
    }

    private static SchedulingProcessFactSnapshot
        CreateHostedFinalAdmissionProcessFacts(
        SchedulingProcessFactSnapshot source,
        HardwareMetricSnapshot hardware,
        Func<SchedulingProcessFact, SchedulingProcessFact>? transform = null)
    {
        Assert.True(SystemMemoryUsageDependency.TryCreate(
            hardware,
            hardware.CapturedAt,
            out var memoryDependency));
        var processes = source.Processes
            .Select(process => transform?.Invoke(process) ?? process)
            .Select(process => process with
            {
                SourceGeneration = source.Generation
            })
            .ToArray();
        var observations = source.DatasetObservations.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        observations[SchedulingProcessMetricMask.MemoryUsage] =
            SchedulingProcessDatasetObservation.CreateCurrent(
                SchedulingProcessMetricMask.MemoryUsage,
                memoryDependency.SourceGeneration,
                memoryDependency.ObservedAtUtcTicks,
                source.Generation,
                source.ObservedAtUtcTicks,
                memoryUsageDependency: memoryDependency);
        return source with
        {
            EmittedCount = checked((uint)processes.Length),
            Processes = processes,
            DatasetObservations = observations
        };
    }

    private static HardwareMetricDatasetObservation
        CreateHardwareDatasetObservation(
            string datasetId,
            SamplingObservationStatus status,
            DateTimeOffset observedAt,
            ulong committedGeneration)
        => new(
            datasetId,
            status,
            SourceGeneration: committedGeneration,
            ObservedAtUtcTicks: observedAt.UtcTicks,
            WorkspaceIdentity: 1,
            ConfigurationGeneration: 1,
            CatalogGeneration: 1,
            CommittedGeneration: committedGeneration)
        {
            LastAttemptAtUtcTicks = observedAt.UtcTicks,
            LastSuccessAtUtcTicks = status == SamplingObservationStatus.Current
                ? observedAt.UtcTicks
                : 0,
            ReadyUntilUtcTicks = DateTimeOffset.MaxValue.UtcTicks,
            FailureCode = status == SamplingObservationStatus.Current
                ? null
                : "fixture-unavailable"
        };

    private static IReadOnlyDictionary<
        SchedulingProcessMetricMask,
        SchedulingProcessDatasetObservation> CreateCurrentDatasetObservations(
        SchedulingProcessMetricMask metrics,
        ulong generation,
        long observedAtUtcTicks)
        => new[]
            {
                SchedulingProcessMetricMask.CpuUsage,
                SchedulingProcessMetricMask.MemoryUsage,
                SchedulingProcessMetricMask.GpuUsage,
                SchedulingProcessMetricMask.GpuDedicatedMemory,
                SchedulingProcessMetricMask.RuntimeState
            }
            .Where(metric => metrics.HasFlag(metric))
            .ToDictionary(
                static metric => metric,
                metric => SchedulingProcessDatasetObservation.CreateCurrent(
                    metric,
                    generation,
                    observedAtUtcTicks,
                    generation,
                    observedAtUtcTicks,
                    memoryUsageDependency:
                        metric == SchedulingProcessMetricMask.MemoryUsage
                            ? TestSystemMemoryUsageDependency.Create(
                                sourceGeneration: generation,
                                observedAtUtcTicks: observedAtUtcTicks,
                                committedGeneration: generation)
                            : null));

    private static SchedulingProcessFactSnapshot CreateScaleProcessFacts(int count)
    {
        const int firstProcessId = 30_000;
        const ulong firstProcessStartKey = 132_537_600_000_000_000;
        var inputs = Enumerable.Range(0, count)
            .Select(static index => (
                ProcessId: firstProcessId + index,
                ProcessStartKey: firstProcessStartKey + checked((ulong)index * 10_000_000UL),
                SoftwareId: $"software:score-only-scale:{index:D4}",
                BaseScore: 20D + index % 70,
                CpuUsagePercent: 10D + index % 80,
                MemoryUsagePercent: 5D + index % 90))
            .ToArray();
        return CreateCompleteProcessFacts(inputs);
    }

    private static DateTimeOffset
        CreateProcessStartedAtOrderedBeforeSoftwareTarget(int processId)
    {
        const string softwareId = "software:cycle-budget:0";
        const long firstFileTime = 133_900_000_000_000_000;
        var softwareKey =
            NativeStableIdentity.CreateCaseInsensitiveKey(softwareId);
        for (var index = 0; index < 4_096; index++)
        {
            var startedAt = DateTimeOffset.FromFileTime(
                checked(firstFileTime + index * TimeSpan.TicksPerSecond));
            var processKey = NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessTargetId(
                    processId,
                    checked((ulong)startedAt.ToFileTime())));
            if (processKey < softwareKey)
            {
                return startedAt;
            }
        }

        throw new InvalidOperationException(
            "The test could not construct a process identity ordered before its fixed software target.");
    }

    private static string CreateSoftwareIdOrderedAfterProcessTarget(
        int processId,
        ulong processStartKey)
    {
        var processTargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessTargetId(
                processId,
                processStartKey));
        for (var index = 0; index < 4_096; index++)
        {
            var softwareId = $"software:cycle-budget:{index}";
            if (NativeStableIdentity.CreateCaseInsensitiveKey(softwareId) > processTargetKey)
            {
                return softwareId;
            }
        }

        throw new InvalidOperationException(
            "The test could not construct a software identity ordered after the process target.");
    }
}
