using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryCleanupPrePonrAdmissionTests
{
    [Fact]
    public void EvaluateCurrentFact_AcceptsExactBackgroundCandidate()
    {
        var fixture = CreateFixture();

        var result = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            fixture.Candidate,
            fixture.Score,
            fixture.Current,
            currentMaximumBaseScore: fixture.Candidate.BaseScore,
            HostManagerRuntimeStates.BackgroundProcess,
            protectionLevel: 0);

        Assert.Equal(HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible, result);
    }

    [Theory]
    [InlineData(HostManagerRuntimeStates.ForegroundFocused)]
    [InlineData(HostManagerRuntimeStates.ForegroundUnfocused)]
    public void EvaluateCurrentFact_RejectsCurrentForegroundState(string runtimeState)
    {
        var fixture = CreateFixture();

        var result = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            fixture.Candidate,
            fixture.Score,
            fixture.Current,
            fixture.Candidate.BaseScore,
            runtimeState,
            protectionLevel: 0);

        Assert.Equal(HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible, result);
    }

    [Theory]
    [InlineData(HostManagerRuntimeStates.ForegroundFocused)]
    [InlineData(HostManagerRuntimeStates.ForegroundUnfocused)]
    public void EvaluateCurrentFact_DefersCandidateWhoseSealedRuntimeWasForeground(
        string baselineRuntimeState)
    {
        var fixture = CreateFixture();
        var baseline = fixture.Candidate with { RuntimeState = baselineRuntimeState };

        var result = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            baseline,
            fixture.Score,
            fixture.Current,
            fixture.Candidate.BaseScore,
            HostManagerRuntimeStates.BackgroundProcess,
            protectionLevel: 0);

        Assert.Equal(HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible, result);
    }

    [Fact]
    public void EvaluateCurrentFact_RejectsIdentityAttributionAndBaseScoreDrift()
    {
        var fixture = CreateFixture();

        AssertKnownIneligible(
            fixture,
            fixture.Current with { ProcessStartKey = fixture.Current.ProcessStartKey + 1 });
        AssertKnownIneligible(
            fixture,
            fixture.Current with { SoftwareId = "software:reattributed" });
        AssertKnownIneligible(
            fixture,
            fixture.Current,
            currentMaximumBaseScore: fixture.Candidate.BaseScore + 1);
        AssertKnownIneligible(
            fixture with { Candidate = fixture.Candidate with { TargetId = "process:foreign" } },
            fixture.Current);
    }

    [Fact]
    public void EvaluateCurrentFact_RejectsAdaptedOrProtectedCandidate()
    {
        var fixture = CreateFixture();

        AssertKnownIneligible(
            fixture,
            fixture.Current with { SoftwareKind = SoftwareKinds.Adapted });

        var protectedResult = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            fixture.Candidate,
            fixture.Score,
            fixture.Current,
            fixture.Candidate.BaseScore,
            HostManagerRuntimeStates.BackgroundProcess,
            OptimizationProtectionLevels.Level2NoOptimization);
        Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible,
            protectedResult);
    }

    [Fact]
    public void EvaluateLiveIdentity_DistinguishesKnownExitFromUnknownRead()
    {
        var fixture = CreateFixture();
        var exact = new ProcessInstanceRecoverySnapshot(
            fixture.Candidate.ProcessId,
            fixture.Candidate.ProcessStartedAt);

        Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible,
            HostManagerMemoryCleanupPrePonrAdmission.EvaluateLiveIdentity(
                fixture.Candidate,
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(exact)));
        Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible,
            HostManagerMemoryCleanupPrePonrAdmission.EvaluateLiveIdentity(
                fixture.Candidate,
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                    nativeErrorCode: 0,
                    "exited")));
        Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible,
            HostManagerMemoryCleanupPrePonrAdmission.EvaluateLiveIdentity(
                fixture.Candidate,
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    exact with { StartedAt = exact.StartedAt.AddTicks(1) })));
        Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown,
            HostManagerMemoryCleanupPrePonrAdmission.EvaluateLiveIdentity(
                fixture.Candidate,
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                    nativeErrorCode: 5,
                    "unavailable")));
    }

    [Fact]
    public void UnknownFinalAdmission_CannotCompleteNormalReleaseRound()
    {
        var result = HostManagerMemoryCleanupExecutionResult.Incomplete(
            HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidatesUnknown,
            candidateCount: 1,
            plannerStateRevision: 10);

        Assert.False(result.CompletedNormalReleaseRound);
        Assert.Equal(0, result.CompletedAtUtcTicks);
        Assert.Equal(
            result,
            result.StampCompletion(DateTimeOffset.UtcNow));
    }

    private static void AssertKnownIneligible(
        AdmissionFixture fixture,
        SchedulingProcessFact current,
        double? currentMaximumBaseScore = null)
        => Assert.Equal(
            HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible,
            HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
                fixture.Candidate,
                fixture.Score,
                current,
                currentMaximumBaseScore ?? fixture.Candidate.BaseScore,
                HostManagerRuntimeStates.BackgroundProcess,
                protectionLevel: 0));

    private static AdmissionFixture CreateFixture()
    {
        const int processId = 42;
        const ulong processStartKey = 133_700_000_000_000_000;
        const string softwareId = "software:pre-ponr";
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var targetId = HostManagerTargetIdentity.CreateProcessTargetId(
            processId,
            processStartKey);
        var candidate = new AutomaticMemoryCleanupCandidate(
            targetId,
            "Pre-PONR candidate",
            processId,
            startedAt,
            HostManagerRuntimeStates.BackgroundProcess,
            BaseScore: 20,
            CpuScore: 0.1,
            MemoryUsedPercent: 10,
            CanApply: true);
        var score = new HostManagerComputeScore(
            NativeComputeScoringOutputKind.ProcessCpu,
            SchedulingGeneration: 7,
            TargetKey: NativeStableIdentity.CreateCaseInsensitiveKey(targetId),
            SoftwareKey: NativeStableIdentity.CreateCaseInsensitiveKey(softwareId),
            processId,
            processStartKey,
            AdapterKey: 0,
            Score: 0.1,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);
        var current = new SchedulingProcessFact(
            processId,
            processStartKey,
            "pre-ponr",
            @"c:\tests\pre-ponr.exe",
            softwareId,
            "Pre-PONR",
            SoftwareKinds.Other,
            "Other",
            BaseScore: 20,
            SchedulingProcessMetricMask.MemoryUsage,
            CpuUsagePercent: 0,
            MemoryUsagePercent: 10,
            SourceGeneration: 2,
            Gpus: []);
        return new AdmissionFixture(candidate, score, current);
    }

    private sealed record AdmissionFixture(
        AutomaticMemoryCleanupCandidate Candidate,
        HostManagerComputeScore Score,
        SchedulingProcessFact Current);
}
