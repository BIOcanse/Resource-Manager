using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.PublicResources;

namespace ResourceManager.App.Tests;

public sealed class HostPublicResourceNormalReleaseEvidenceTrackerTests
{
    [Fact]
    public void TwoCompletedRoundsRequireDistinctActionAfterSamples()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        var first = CreateCycle(1, capturedAtSecond: 1);
        var second = CreateCycle(2, capturedAtSecond: 2);
        var third = CreateCycle(3, capturedAtSecond: 3);

        Assert.False(tracker.BeginCycle(first, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(first, CompleteAt(second: 1.5));
        Assert.False(tracker.BeginCycle(second, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(second, CompleteAt(second: 2.5));

        Assert.True(tracker.BeginCycle(third, Shortage).AfterNormalReleaseRounds);
    }

    [Fact]
    public void ReusedGenerationAndPreCompletionCaptureCannotAdvanceProof()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        var first = CreateCycle(1, capturedAtSecond: 1);
        tracker.BeginCycle(first, Shortage);
        tracker.CompleteCycle(first, CompleteAt(second: 2));

        var sameGeneration = CreateCycle(1, capturedAtSecond: 3);
        Assert.False(tracker.BeginCycle(sameGeneration, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(sameGeneration, CompleteAt(second: 3.5));

        var committedBeforeCompletion = CreateCycle(2, capturedAtSecond: 1.5);
        Assert.False(tracker.BeginCycle(
            committedBeforeCompletion,
            Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(committedBeforeCompletion, CompleteAt(second: 4));

        var validPostSample = CreateCycle(2, capturedAtSecond: 3);
        Assert.False(tracker.BeginCycle(validPostSample, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(validPostSample, CompleteAt(second: 3.5));
        var third = CreateCycle(3, capturedAtSecond: 4);

        Assert.True(tracker.BeginCycle(third, Shortage).AfterNormalReleaseRounds);
    }

    [Fact]
    public void IncompleteRoundBreaksUnmaturedChain()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        var first = CreateCycle(1, capturedAtSecond: 1);
        tracker.BeginCycle(first, Shortage);
        tracker.CompleteCycle(first, CompleteAt(second: 1.5));

        var second = CreateCycle(2, capturedAtSecond: 2);
        Assert.False(tracker.BeginCycle(second, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(
            second,
            HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown));

        var third = CreateCycle(3, capturedAtSecond: 3);
        Assert.False(tracker.BeginCycle(third, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(third, CompleteAt(second: 3.5));
        var fourth = CreateCycle(4, capturedAtSecond: 4);

        Assert.False(tracker.BeginCycle(fourth, Shortage).AfterNormalReleaseRounds);
    }

    [Fact]
    public void MalformedCompleteDispositionCannotAdvanceProof()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        var first = CreateCycle(1, capturedAtSecond: 1);
        tracker.BeginCycle(first, Shortage);
        tracker.CompleteCycle(
            first,
            new HostManagerMemoryCleanupExecutionResult(
                HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull,
                CandidateCount: 1,
                BlockedCandidateCount: 0,
                PlannerStateRevision: 1,
                PlannedCount: 1,
                RequestedCount: 1,
                TerminalCount: 0,
                SucceededCount: 0,
                CompletedAtUtcTicks: AtSecond(1.5).UtcTicks));

        var second = CreateCycle(2, capturedAtSecond: 2);
        Assert.False(tracker.BeginCycle(second, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(second, CompleteAt(second: 2.5));
        var third = CreateCycle(3, capturedAtSecond: 3);

        Assert.False(tracker.BeginCycle(third, Shortage).AfterNormalReleaseRounds);
    }

    [Fact]
    public void MatureProofLatchesUntilCapacityOrBindingChanges()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        CompleteRound(tracker, CreateCycle(1, capturedAtSecond: 1), completedAtSecond: 1.5);
        CompleteRound(tracker, CreateCycle(2, capturedAtSecond: 2), completedAtSecond: 2.5);

        var mature = CreateCycle(3, capturedAtSecond: 3);
        Assert.True(tracker.BeginCycle(mature, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(mature, default);
        Assert.True(tracker.BeginCycle(
            CreateCycle(4, capturedAtSecond: 4),
            Shortage).AfterNormalReleaseRounds);

        Assert.True(tracker.BeginCycle(
            CreateCycle(5, capturedAtSecond: 5),
            HostPublicResourceCapacityObservation.HealthyFresh).RecallAllowed);
        Assert.False(tracker.BeginCycle(
            CreateCycle(6, capturedAtSecond: 6),
            Shortage).AfterNormalReleaseRounds);

        var changedPlan = CreateCycle(7, capturedAtSecond: 7) with
        {
            RuntimePlanVersion = 2
        };
        Assert.False(tracker.BeginCycle(changedPlan, Shortage).AfterNormalReleaseRounds);
    }

    [Fact]
    public void NativeHostSessionChangeResetsMatureProof()
    {
        var tracker = new HostPublicResourceNormalReleaseEvidenceTracker();
        CompleteRound(tracker, CreateCycle(1, capturedAtSecond: 1), completedAtSecond: 1.5);
        CompleteRound(tracker, CreateCycle(2, capturedAtSecond: 2), completedAtSecond: 2.5);

        var changedSession = CreateCycle(3, capturedAtSecond: 3) with
        {
            NativeHostSessionIncarnation = 2
        };

        Assert.False(tracker.BeginCycle(changedSession, Shortage).AfterNormalReleaseRounds);
    }

    private static void CompleteRound(
        HostPublicResourceNormalReleaseEvidenceTracker tracker,
        HostPublicResourceNormalReleaseCycle cycle,
        double completedAtSecond)
    {
        Assert.False(tracker.BeginCycle(cycle, Shortage).AfterNormalReleaseRounds);
        tracker.CompleteCycle(cycle, CompleteAt(completedAtSecond));
    }

    private static HostManagerMemoryCleanupExecutionResult CompleteAt(double second)
        => HostManagerMemoryCleanupExecutionResult
            .CompleteNoDecision(candidateCount: 0, plannerStateRevision: 1)
            .StampCompletion(AtSecond(second));

    private static HostPublicResourceNormalReleaseCycle CreateCycle(
        ulong committedGeneration,
        double capturedAtSecond)
        => new(
            RuntimePlanVersion: 1,
            HostPublicationSequence: 1,
            NativeHostSessionIncarnation: 1,
            HostPlanEpoch: 1,
            HostPlanSha256: new string('A', 64),
            MemoryCleanupConfigurationGeneration: 1,
            SmartConfigurationGeneration: 1,
            SmartConfigurationSha256: new string('B', 64),
            NormalMemoryReleaseEnabled: true,
            HardwareWorkspaceIdentity: 1,
            HardwareConfigurationGeneration: 1,
            HardwareCatalogGeneration: 1,
            HardwareCommittedGeneration: committedGeneration,
            HardwareCapturedAtUtcTicks: AtSecond(capturedAtSecond).UtcTicks);

    private static DateTimeOffset AtSecond(double second)
        => DateTimeOffset.UnixEpoch.AddTicks(
            checked((long)(second * TimeSpan.TicksPerSecond)));

    private static HostPublicResourceCapacityObservation Shortage { get; } =
        HostPublicResourceCapacityObservation.ShortageFresh();
}
