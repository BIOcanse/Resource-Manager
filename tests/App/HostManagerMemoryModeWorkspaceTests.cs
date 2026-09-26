using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace ResourceManager.App.Tests;

public sealed class HostManagerMemoryModeWorkspaceTests
{
    private static readonly ulong HighKey = Key("software-high");
    private static readonly ulong LowKey = Key("software-low");
    private static readonly ulong MiddleKey = Key("software-middle");
    private static readonly ulong MixedKey = Key("software-mixed");

    [Fact]
    public void PlanUsesOnlySoftwareCpuScoresForMemoryOrdering()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);

        var result = workspace.Plan(
            CreateScoring(includeGpu: true),
            CreateProcessFacts(),
            new HostManagerMemorySourceStamp(7, 20),
            memoryFreeRatioUnits: 2_000,
            snapshotGeneration: 80,
            allowUnrestricted: true);

        Assert.Equal(3, result.Software.Length);
        Assert.Equal(NativeMemoryMode.Normal, Find(result, HighKey).Mode);
        Assert.Equal(NativeMemoryMode.Optimize, Find(result, LowKey).Mode);
        Assert.Equal(NativeMemoryMode.Optimize, Find(result, MiddleKey).Mode);
        Assert.Equal(100D, Find(result, HighKey).CpuScore);
        Assert.Equal(2U, result.OptimizeCount);
        Assert.Equal(0U, result.StrongestCount);
        Assert.Equal(NativeMemoryGradeSet.Normal, Find(result, HighKey).BaseScoreAllowedGrades);
        Assert.Equal(60D, Find(result, LowKey).BaseScore);
        Assert.Equal(0D, Find(result, LowKey).CpuScore);
        Assert.Equal(NativeMemoryGradeSet.Normal | NativeMemoryGradeSet.L1 | NativeMemoryGradeSet.L2 | NativeMemoryGradeSet.L3,
            Find(result, LowKey).BaseScoreAllowedGrades);
        Assert.Equal(NativeMemoryGradeSet.Normal | NativeMemoryGradeSet.L1 | NativeMemoryGradeSet.L2 | NativeMemoryGradeSet.L3,
            Find(result, MiddleKey).BaseScoreAllowedGrades);
        var diagnostics = Assert.IsType<HostManagerMemoryModeDiagnostics>(
            HostManagerSmartCoordinator.ProjectMemoryModeDiagnostics(result));
        Assert.Equal(["Normal"], diagnostics.Software.Single(row => row.SoftwareKey == HighKey.ToString()).BaseScoreAllowedGrades);
        Assert.Equal(["Normal", "L1", "L2", "L3"],
            diagnostics.Software.Single(row => row.SoftwareKey == MiddleKey.ToString()).BaseScoreAllowedGrades);
        Assert.Equal(["Normal", "L1", "L2", "L3"],
            diagnostics.Software.Single(row => row.SoftwareKey == LowKey.ToString()).BaseScoreAllowedGrades);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(diagnostics,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.All(wire.GetProperty("software").EnumerateArray(), row =>
            Assert.True(row.GetProperty("baseScoreAllowedGrades").GetArrayLength() > 0));
    }

    [Fact]
    public void PlanDoesNotRequireAGpuScoringDomain()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);

        var result = workspace.Plan(
            CreateScoring(includeGpu: false),
            CreateProcessFacts(),
            new HostManagerMemorySourceStamp(7, 20),
            memoryFreeRatioUnits: 2_000,
            snapshotGeneration: 80,
            allowUnrestricted: false);

        Assert.Equal(3, result.Software.Length);
        Assert.Equal(0U, Find(result, LowKey).Rank);
        Assert.Equal(NativeMemoryMode.Optimize, Find(result, LowKey).Mode);
    }

    [Fact]
    public void PlanRejectsMissingOrCrossGenerationCpuScores()
    {
        var configuration = CreateConfiguration();
        using var missingWorkspace = new HostManagerMemoryModeWorkspace(in configuration);
        Assert.Throws<InvalidDataException>(() => missingWorkspace.Plan(
            new HostManagerComputeScoringCycleResult(10, null, null),
            CreateProcessFacts(),
            new HostManagerMemorySourceStamp(7, 20),
            2_000,
            80,
            true));

        using var generationWorkspace = new HostManagerMemoryModeWorkspace(in configuration);
        var scoring = CreateScoring(includeGpu: false) with { SchedulingGeneration = 11 };
        Assert.Throws<InvalidDataException>(() => generationWorkspace.Plan(
            scoring,
            CreateProcessFacts(),
            new HostManagerMemorySourceStamp(7, 20),
            2_000,
            80,
            true));
    }

    [Fact]
    public void PlanUsesHighestProcessBaseScoreForSoftwarePermissions()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);
        var cpu = ImmutableArray.Create(
            ProcessScore(NativeComputeScoringOutputKind.ProcessCpu, MixedKey, 0, 0),
            ProcessScore(NativeComputeScoringOutputKind.ProcessCpu, MixedKey, 0, 0, processId: 2),
            SoftwareScore(
                NativeComputeScoringOutputKind.SoftwareCpu,
                MixedKey,
                adapterKey: 0,
                score: 0,
                memberCount: 2));
        var scoring = new HostManagerComputeScoringCycleResult(
            SchedulingGeneration: 10,
            Cpu: CreateCpuDomain(cpu),
            Gpu: null);

        var result = workspace.Plan(
            scoring,
            CreateProcessFactsFor(("software-mixed", 10), ("software-mixed", 81)),
            new HostManagerMemorySourceStamp(7, 20),
            memoryFreeRatioUnits: 500,
            snapshotGeneration: 80,
            allowUnrestricted: true);

        var desired = Assert.Single(result.Software);
        Assert.Equal(MixedKey, desired.SoftwareKey);
        Assert.Equal(81D, desired.BaseScore);
        Assert.Equal(NativeMemoryGradeSet.Normal, desired.BaseScoreAllowedGrades);
        Assert.True(desired.BaseScoreClamped);
        Assert.Equal(NativeMemoryMode.Normal, desired.Mode);
        Assert.Equal(0U, result.StrongestCount);
    }

    [Fact]
    public void PlanPartitionsCommittedGenerationByWorkspaceIdentity()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);
        var processFacts = CreateProcessFacts();

        var first = workspace.Plan(
            CreateScoring(includeGpu: false),
            processFacts,
            new HostManagerMemorySourceStamp(41, 44),
            2_000,
            80,
            false);
        var second = workspace.Plan(
            WithSchedulingGeneration(CreateScoring(includeGpu: false), 11),
            processFacts,
            new HostManagerMemorySourceStamp(45, 1),
            2_000,
            81,
            false);

        Assert.Equal(new HostManagerMemorySourceStamp(41, 44), first.MemorySource);
        Assert.Equal(new HostManagerMemorySourceStamp(45, 1), second.MemorySource);
        Assert.Throws<InvalidOperationException>(() => workspace.Plan(
            WithSchedulingGeneration(CreateScoring(includeGpu: false), 12),
            processFacts,
            new HostManagerMemorySourceStamp(41, 100),
            2_000,
            82,
            false));
    }

    [Fact]
    public void WorkspaceIdentityIncludesBaseScorePermissionThresholds()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);
        var changedThresholds = NativeMemoryModeConfigurationWriter.Create(
            generation: configuration.Generation,
            maximumSoftwareCount: configuration.MaximumSoftwareCount,
            ratioUnitsMaximum: configuration.RatioUnitsMaximum,
            unrestrictedMinimumFreeRatioUnits:
                configuration.UnrestrictedMinimumFreeRatioUnits,
            normalMinimumFreeRatioUnits: configuration.NormalMinimumFreeRatioUnits,
            strongBeginFreeRatioUnits: configuration.StrongBeginFreeRatioUnits,
            middleTierMinimumBaseScore: 22,
            highTierMinimumBaseScore: 81);

        Assert.False(workspace.MatchesConfiguration(in changedThresholds));
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("process")]
    [InlineData("start")]
    [InlineData("software")]
    [InlineData("duplicate")]
    public void PlanRejectsUnboundObservedProcessMembers(string changedField)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerMemoryModeWorkspace(in configuration);
        var scoring = CreateScoring(includeGpu: false);
        var cpu = scoring.Cpu!;
        var process = cpu.Scores[0];
        var changed = changedField switch
        {
            "inventory" => cpu with
            {
                SourceIdentity = cpu.SourceIdentity with { InventoryGeneration = 21 }
            },
            "process" => cpu with { Scores = cpu.Scores.SetItem(0, process with { ProcessId = 999 }) },
            "start" => cpu with { Scores = cpu.Scores.SetItem(0, process with { ProcessStartKey = 999 }) },
            "software" => cpu with { Scores = cpu.Scores.SetItem(0, process with { SoftwareKey = LowKey }) },
            "duplicate" => cpu with { Scores = cpu.Scores.Add(process) },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        Assert.Throws<InvalidDataException>(() => workspace.Plan(
            scoring with { Cpu = changed }, CreateProcessFacts(),
            new HostManagerMemorySourceStamp(7, 20), 2_000, 80, true));
    }

    private static NativeMemoryModeConfiguration CreateConfiguration()
        => NativeMemoryModeConfigurationWriter.Create(
            generation: 1,
            maximumSoftwareCount: 3,
            ratioUnitsMaximum: 10_000,
            unrestrictedMinimumFreeRatioUnits: 6_000,
            normalMinimumFreeRatioUnits: 3_000,
            strongBeginFreeRatioUnits: 1_000,
            middleTierMinimumBaseScore: 21,
            highTierMinimumBaseScore: 81);

    private static HostManagerComputeScoringCycleResult CreateScoring(bool includeGpu)
    {
        const ulong generation = 10;
        var cpu = ImmutableArray.Create(
            ProcessScore(NativeComputeScoringOutputKind.ProcessCpu, HighKey, 0, 999),
            ProcessScore(NativeComputeScoringOutputKind.ProcessCpu, LowKey, 0, 0, processId: 2),
            ProcessScore(NativeComputeScoringOutputKind.ProcessCpu, MiddleKey, 0, 50, processId: 3),
            SoftwareScore(NativeComputeScoringOutputKind.SoftwareCpu, HighKey, 0, 100),
            SoftwareScore(NativeComputeScoringOutputKind.SoftwareCpu, LowKey, 0, 0),
            SoftwareScore(NativeComputeScoringOutputKind.SoftwareCpu, MiddleKey, 0, 50));
        HostManagerComputeScoreDomainSnapshot? gpu = includeGpu
            ? new(
                generation,
                30,
                40,
                50,
                ImmutableArray.Create(
                    SoftwareScore(NativeComputeScoringOutputKind.SoftwareGpu, HighKey, 100, 0),
                    SoftwareScore(NativeComputeScoringOutputKind.SoftwareGpu, LowKey, 100, 100)))
            : null;
        return new(
            generation,
            CreateCpuDomain(cpu),
            gpu);
    }

    private static HostManagerComputeScoreDomainSnapshot CreateCpuDomain(
        ImmutableArray<HostManagerComputeScore> scores)
    {
        var cpu = new HostManagerComputeScoreDomainSnapshot(10, 91, 0, 0, scores);
        return cpu with { SourceIdentity = cpu.SourceIdentity with { InventoryGeneration = 20 } };
    }

    private static HostManagerComputeScoringCycleResult WithSchedulingGeneration(
        HostManagerComputeScoringCycleResult source,
        ulong generation)
    {
        var cpu = source.Cpu!;
        return source with
        {
            SchedulingGeneration = generation,
            Cpu = cpu with
            {
                SchedulingGeneration = generation,
                Scores = cpu.Scores
                    .Select(row => row with { SchedulingGeneration = generation })
                    .ToImmutableArray()
            }
        };
    }

    private static HostManagerComputeScore SoftwareScore(
        NativeComputeScoringOutputKind kind,
        ulong softwareKey,
        ulong adapterKey,
        double score,
        uint memberCount = 1)
        => new(
            kind,
            10,
            0,
            softwareKey,
            0,
            0,
            adapterKey,
            score,
            memberCount,
            NativeComputeScoringRuntimeState.Unknown);

    private static HostManagerComputeScore ProcessScore(
        NativeComputeScoringOutputKind kind,
        ulong softwareKey,
        ulong adapterKey,
        double score,
        int processId = 1)
        => new(
            kind,
            10,
            123,
            softwareKey,
            processId,
            checked((ulong)processId),
            adapterKey,
            score,
            1,
            NativeComputeScoringRuntimeState.Unknown);

    private static HostManagerDesiredMemoryMode Find(
        HostManagerMemoryModeDesiredSnapshot snapshot,
        ulong softwareKey)
        => Assert.Single(snapshot.Software.Where(row => row.SoftwareKey == softwareKey));

    private static SchedulingProcessFactSnapshot CreateProcessFacts()
        => CreateProcessFactsFor(
            ("software-high", 81),
            ("software-low", 60),
            ("software-middle", 61));

    private static SchedulingProcessFactSnapshot CreateProcessFactsFor(
        params (string SoftwareId, double BaseScore)[] rows)
    {
        var processes = rows
            .Select((row, index) => ProcessFact(index + 1, row.SoftwareId, row.BaseScore))
            .ToArray();
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        return new(
            SamplingObservationStatus.Current,
            Generation: 20,
            ObservedAtUtcTicks: observedAt,
            EnumeratedCount: checked((uint)processes.Length),
            EmittedCount: checked((uint)processes.Length),
            SkippedCount: 0,
            OverflowCount: 0,
            RequestedMetricMask: SchedulingProcessMetricMask.CpuUsage,
            CurrentMetricMask: SchedulingProcessMetricMask.CpuUsage,
            Processes: processes)
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.CpuUsage,
                        sourceGeneration: 20,
                        observedAtUtcTicks: observedAt,
                        inventoryGeneration: 20,
                        inventoryObservedAtUtcTicks: observedAt)
            }
        };
    }

    private static SchedulingProcessFact ProcessFact(
        int processId,
        string softwareId,
        double baseScore)
        => new(
            processId,
            checked((ulong)processId),
            $"process-{processId}",
            null,
            softwareId,
            softwareId,
            "other",
            "other",
            baseScore,
            SchedulingProcessMetricMask.CpuUsage,
            CpuUsagePercent: 1,
            MemoryUsagePercent: 0,
            SourceGeneration: 20,
            Gpus: []);

    private static ulong Key(string softwareId)
        => NativeStableIdentity.CreateCaseInsensitiveKey(softwareId);
}
