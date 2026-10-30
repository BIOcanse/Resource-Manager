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
    [Theory]
    [InlineData(80, 81, 1)]
    [InlineData(81, 81, 2)]
    [InlineData(90, 81, 2)]
    [InlineData(85, 90, 1)]
    public void EffectiveHighTierBlocksCleanupEvenWithoutProtection(double score, double highMinimum,
        int expected)
    {
        var fixture = CreateFixture();
        var result = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            fixture.Candidate with { BaseScore = score }, fixture.Score,
            fixture.Current with { BaseScore = score }, score,
            HostManagerRuntimeStates.BackgroundProcess, false, highMinimum);
        Assert.Equal((HostManagerMemoryCleanupCandidateAdmissionStatus)expected, result);
    }

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
            memorySchedulingExcluded: false);

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
            memorySchedulingExcluded: false);

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
            memorySchedulingExcluded: false);

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
    public void EvaluateCurrentFact_RejectsAdaptedCandidate()
    {
        var fixture = CreateFixture();

        AssertKnownIneligible(
            fixture,
            fixture.Current with { SoftwareKind = SoftwareKinds.Adapted });
    }

    [Fact]
    public void EvaluateCurrentFact_RejectsCandidateExcludedFromMemorySchedulingWithoutProtection()
    {
        var fixture = CreateFixture();

        var result = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
            fixture.Candidate,
            fixture.Score,
            fixture.Current,
            fixture.Candidate.BaseScore,
            HostManagerRuntimeStates.BackgroundProcess,
            memorySchedulingExcluded: true);

        Assert.Equal(HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible, result);
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
                memorySchedulingExcluded: false));

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
            RoundNumber: 7,
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
            "other",
            BaseScore: 20,
            SchedulingProcessMetricMask.MemoryUsage,
            CpuUsagePercent: 0,
            MemoryUsedBytes: 10,
            SourceGeneration: 2,
            Gpus: []);
        return new AdmissionFixture(candidate, score, current);
    }

    private sealed record AdmissionFixture(
        AutomaticMemoryCleanupCandidate Candidate,
        HostManagerComputeScore Score,
        SchedulingProcessFact Current);
}
