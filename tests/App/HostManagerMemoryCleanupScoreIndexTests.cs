using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryCleanupScoreIndexTests
{
    [Fact]
    public void TryCreate_AcceptsOnlyTheExactCurrentProcessCpuSet()
    {
        var compute = CreateCompute(
            sourceGeneration: 20,
            ProcessScore(1, 101, 1001, 11),
            ProcessScore(2, 202, 2002, 22),
            SoftwareScore(3003, 33));

        Assert.True(HostManagerMemoryCleanupScoreIndex.TryCreate(
            compute,
            processInventoryGeneration: 20,
            expectedTargets: ExpectedTargets((1, 101), (2, 202)),
            out var scores));
        Assert.Equal(2, scores.Count);
        Assert.Equal(11, scores[new(1, 101)].Score);
        Assert.Equal(22, scores[new(2, 202)].Score);
    }

    [Fact]
    public void TryCreate_FailsClosedForStaleOrIncompleteProcessCpuSet()
    {
        var compute = CreateCompute(
            sourceGeneration: 20,
            ProcessScore(1, 101, 1001, 11));

        Assert.False(HostManagerMemoryCleanupScoreIndex.TryCreate(
            compute,
            processInventoryGeneration: 21,
            expectedTargets: ExpectedTargets((1, 101)),
            out var stale));
        Assert.Empty(stale);

        Assert.False(HostManagerMemoryCleanupScoreIndex.TryCreate(
            compute,
            processInventoryGeneration: 20,
            expectedTargets: ExpectedTargets((1, 101), (2, 202)),
            out var incomplete));
        Assert.Empty(incomplete);
    }

    [Fact]
    public void TryCreate_MatchesInventoryWithoutRequiringThePhysicalWindowToShareItsGeneration()
    {
        var compute = CreateCompute(20, ProcessScore(1, 101, 1001, 11));
        compute = compute with
        {
            Cpu = compute.Cpu! with
            {
                SourceIdentity = compute.Cpu!.SourceIdentity with { MetricGeneration = 91 }
            }
        };
        Assert.True(HostManagerMemoryCleanupScoreIndex.TryCreate(
            compute, 20, ExpectedTargets((1, 101)), out var scores));
        Assert.Single(scores);
        Assert.False(HostManagerMemoryCleanupScoreIndex.TryCreate(
            compute, 91, ExpectedTargets((1, 101)), out _));
    }

    [Fact]
    public void CandidateProjection_UsesTheSoftwareMemberMaximumBaseScore()
    {
        const ulong firstStartKey = 100_001;
        const ulong secondStartKey = 100_002;
        var targets = new HostManagerMemoryCleanupTargetFact[]
        {
            new(
                "process:1",
                "software-a",
                "Process 1",
                1,
                firstStartKey,
                DateTimeOffset.FromFileTime(checked((long)firstStartKey)),
                "background",
                BaseScore: 70,
                CanApply: true),
            new(
                "process:2",
                "software-a",
                "Process 2",
                2,
                secondStartKey,
                DateTimeOffset.FromFileTime(checked((long)secondStartKey)),
                "background",
                BaseScore: 81,
                CanApply: true)
        };
        var scores = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>
        {
            [new(1, firstStartKey)] = ProjectionScore(
                targets[0],
                score: 11),
            [new(2, secondStartKey)] = ProjectionScore(
                targets[1],
                score: 22)
        };

        var candidates = HostManagerMemoryCleanupCandidateProjection.Create(targets, scores);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(81, candidate.BaseScore));
        Assert.Equal(11, candidates[0].CpuScore);
        Assert.Equal(22, candidates[1].CpuScore);
        Assert.All(candidates, candidate => Assert.Equal(0, candidate.MemoryUsedPercent));
    }

    [Fact]
    public void CandidateProjection_PreservesSoftwareMaximumWhenHighScoreMemberCannotApply()
    {
        const ulong protectedStartKey = 200_001;
        const ulong liveStartKey = 200_002;
        var targets = new HostManagerMemoryCleanupTargetFact[]
        {
            new(
                "process:protected",
                "software-a",
                "Protected process",
                1,
                protectedStartKey,
                DateTimeOffset.FromFileTime(checked((long)protectedStartKey)),
                "background",
                BaseScore: 81,
                CanApply: false),
            new(
                "process:live",
                "software-a",
                "Live process",
                2,
                liveStartKey,
                DateTimeOffset.FromFileTime(checked((long)liveStartKey)),
                "background",
                BaseScore: 20,
                CanApply: true)
        };
        var scores = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>
        {
            [new(1, protectedStartKey)] = ProjectionScore(targets[0], score: 11),
            [new(2, liveStartKey)] = ProjectionScore(targets[1], score: 22)
        };

        var candidates = HostManagerMemoryCleanupCandidateProjection.Create(targets, scores);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(81, candidate.BaseScore));
        Assert.False(candidates[0].CanApply);
        Assert.True(candidates[1].CanApply);
    }

    private static HostManagerComputeScoringCycleResult CreateCompute(
        ulong sourceGeneration,
        params HostManagerComputeScore[] scores)
        => new(
            SchedulingGeneration: 10,
            Cpu: new(
                SchedulingGeneration: 10,
                SourceGeneration: sourceGeneration,
                TopologyGeneration: 0,
                TopologyFingerprint: 0,
                scores.ToImmutableArray()),
            Gpu: null);

    private static HostManagerComputeScore ProcessScore(
        int processId,
        ulong processStartKey,
        ulong softwareKey,
        double score)
        => new(
            NativeComputeScoringOutputKind.ProcessCpu,
            SchedulingGeneration: 10,
            TargetKey: checked((ulong)processId + 10_000),
            SoftwareKey: softwareKey,
            ProcessId: processId,
            ProcessStartKey: processStartKey,
            AdapterKey: 0,
            Score: score,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);

    private static HostManagerComputeScore SoftwareScore(
        ulong softwareKey,
        double score)
        => new(
            NativeComputeScoringOutputKind.SoftwareCpu,
            SchedulingGeneration: 10,
            TargetKey: softwareKey,
            SoftwareKey: softwareKey,
            ProcessId: 0,
            ProcessStartKey: 0,
            AdapterKey: 0,
            Score: score,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);

    private static HostManagerComputeScore ProjectionScore(
        HostManagerMemoryCleanupTargetFact target,
        double score)
        => new(
            NativeComputeScoringOutputKind.ProcessCpu,
            SchedulingGeneration: 10,
            TargetKey: NativeStableIdentity.CreateCaseInsensitiveKey(target.TargetId),
            SoftwareKey: NativeStableIdentity.CreateCaseInsensitiveKey(target.SoftwareId),
            ProcessId: target.ProcessId,
            ProcessStartKey: target.ProcessStartKey,
            AdapterKey: 0,
            Score: score,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);

    private static IReadOnlyCollection<HostManagerMemoryCleanupTargetFact> ExpectedTargets(
        params (int ProcessId, ulong ProcessStartKey)[] identities)
        => identities
            .Select(identity => new HostManagerMemoryCleanupTargetFact(
                $"process:{identity.ProcessId}",
                "software",
                $"Process {identity.ProcessId}",
                identity.ProcessId,
                identity.ProcessStartKey,
                DateTimeOffset.FromFileTime(checked((long)identity.ProcessStartKey)),
                "background",
                BaseScore: 20,
                CanApply: true))
            .ToArray();
}
