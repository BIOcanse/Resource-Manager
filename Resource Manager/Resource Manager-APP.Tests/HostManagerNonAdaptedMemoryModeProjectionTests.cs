using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using Xunit;

namespace ResourceManager.App.Tests;

public sealed class HostManagerNonAdaptedMemoryModeProjectionTests
{
    private const ulong SchedulingGeneration = 71;
    private const ulong InventoryGeneration = 901;
    private const ulong CpuSourceGeneration = 902;

    [Theory]
    [InlineData(
        (byte)NativeMemoryMode.Unrestricted,
        (byte)HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
        0U)]
    [InlineData(
        (byte)NativeMemoryMode.Normal,
        (byte)HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
        0U)]
    [InlineData(
        (byte)NativeMemoryMode.Optimize,
        (byte)HostManagerNonAdaptedMemoryProcessAction.Optimize,
        3U)]
    [InlineData(
        (byte)NativeMemoryMode.PagedFrozen,
        (byte)HostManagerNonAdaptedMemoryProcessAction.PagedFrozen,
        1U)]
    public void Create_MapsOnlyEligibleNonAdaptedProcesses(
        byte modeValue,
        byte expectedActionValue,
        uint expectedMemoryPriority)
    {
        var mode = (NativeMemoryMode)modeValue;
        var expectedAction = (HostManagerNonAdaptedMemoryProcessAction)expectedActionValue;
        var fixture = CreateFixture(mode);

        var result = HostManagerNonAdaptedMemoryModeProjection.Create(
            fixture.Compute,
            fixture.MemoryModes,
            fixture.ProcessFacts,
            fixture.Capabilities,
            optimizeMemoryPriority: 3,
            pagedFrozenMemoryPriority: 1);

        Assert.Equal(SchedulingGeneration, result.SchedulingGeneration);
        Assert.Equal(InventoryGeneration, result.InventoryGeneration);
        Assert.Equal(CpuSourceGeneration, result.CpuSourceGeneration);
        var directive = Assert.Single(result.Processes);
        Assert.Equal("software-a", directive.SoftwareId);
        Assert.Equal(101, directive.ProcessId);
        Assert.Equal(1001UL, directive.ProcessStartKey);
        Assert.Equal(
            NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(101, 1001)),
            directive.MemoryPolicyTargetKey);
        Assert.NotEqual(
            NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessTargetId(101, 1001)),
            directive.MemoryPolicyTargetKey);
        Assert.Equal(mode, directive.Mode);
        Assert.Equal(expectedAction, directive.Action);
        Assert.Equal(
            expectedMemoryPriority == 0 ? null : expectedMemoryPriority,
            directive.TargetMemoryPriority);
    }

    [Fact]
    public void Create_OrdersEligibleProcessesBySoftwareRankThenProcessIdentity()
    {
        var fixture = CreateFixture(NativeMemoryMode.Optimize, secondEligible: true);
        var reversedModes = fixture.MemoryModes with
        {
            Software = fixture.MemoryModes.Software.Reverse().ToImmutableArray()
        };

        var result = HostManagerNonAdaptedMemoryModeProjection.Create(
            fixture.Compute,
            reversedModes,
            fixture.ProcessFacts,
            fixture.Capabilities,
            optimizeMemoryPriority: 3,
            pagedFrozenMemoryPriority: 1);

        Assert.Collection(
            result.Processes,
            first =>
            {
                Assert.Equal(101, first.ProcessId);
                Assert.Equal(0U, first.SoftwareRank);
            },
            second =>
            {
                Assert.Equal(202, second.ProcessId);
                Assert.Equal(1U, second.SoftwareRank);
            });
    }

    [Fact]
    public void Create_AllowsIndependentScalarCpuSourceGeneration()
    {
        var fixture = CreateFixture(NativeMemoryMode.Normal);
        var independentFacts = fixture.ProcessFacts with
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    fixture.ProcessFacts.DatasetObservations[
                        SchedulingProcessMetricMask.CpuUsage] with
                    {
                        SourceGeneration = CpuSourceGeneration + 1
                    }
            }
        };

        var result = HostManagerNonAdaptedMemoryModeProjection.Create(
            fixture.Compute,
            fixture.MemoryModes,
            independentFacts,
            fixture.Capabilities,
            optimizeMemoryPriority: 3,
            pagedFrozenMemoryPriority: 1);
        Assert.Equal(CpuSourceGeneration, result.CpuSourceGeneration);
        Assert.Equal(InventoryGeneration, result.InventoryGeneration);
        Assert.Single(result.Processes);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("processStart")]
    [InlineData("software")]
    public void Create_RejectsScoredInventoryOrProcessDrift(string changedField)
    {
        var fixture = CreateFixture(NativeMemoryMode.Normal);
        var cpu = fixture.Compute.Cpu!;
        var process = cpu.Scores[0];
        var changed = changedField switch
        {
            "inventory" => cpu with
            {
                SourceIdentity = cpu.SourceIdentity with { InventoryGeneration = InventoryGeneration + 1 }
            },
            "processStart" => cpu with
            {
                Scores = cpu.Scores.SetItem(0, process with { ProcessStartKey = process.ProcessStartKey + 1 })
            },
            "software" => cpu with
            {
                Scores = cpu.Scores.SetItem(0, process with
                {
                    SoftwareKey = NativeStableIdentity.CreateCaseInsensitiveKey("software-b")
                })
            },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        Assert.Throws<InvalidDataException>(() => HostManagerNonAdaptedMemoryModeProjection.Create(
            fixture.Compute with { Cpu = changed }, fixture.MemoryModes,
            fixture.ProcessFacts, fixture.Capabilities, 3, 1));
    }

    [Fact]
    public void Create_RejectsIncompleteSoftwareMembership()
    {
        var fixture = CreateFixture(NativeMemoryMode.Normal);
        var softwareAKey = NativeStableIdentity.CreateCaseInsensitiveKey("software-a");
        var alteredCpu = fixture.Compute.Cpu! with
        {
            Scores = fixture.Compute.Cpu.Scores
                .Select(score => score.Kind == NativeComputeScoringOutputKind.SoftwareCpu
                        && score.SoftwareKey == softwareAKey
                    ? score with { MemberCount = 2 }
                    : score)
                .ToImmutableArray()
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeProjection.Create(
                fixture.Compute with { Cpu = alteredCpu },
                fixture.MemoryModes,
                fixture.ProcessFacts,
                fixture.Capabilities,
                optimizeMemoryPriority: 3,
                pagedFrozenMemoryPriority: 1));
    }

    [Fact]
    public void Create_RejectsConflictingAutomaticAndAdaptedCapabilities()
    {
        var fixture = CreateFixture(NativeMemoryMode.Normal);
        var capabilities = fixture.Capabilities
            .Select(capability => capability.ProcessId == 101
                ? capability with { CanApplyAdaptedPolicy = true }
                : capability)
            .ToArray();

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeProjection.Create(
                fixture.Compute,
                fixture.MemoryModes,
                fixture.ProcessFacts,
                capabilities,
                optimizeMemoryPriority: 3,
                pagedFrozenMemoryPriority: 1));
    }

    [Theory]
    [InlineData(0U, 1U)]
    [InlineData(6U, 1U)]
    [InlineData(3U, 0U)]
    [InlineData(3U, 4U)]
    public void Create_RejectsInvalidMemoryPriorityPolicy(
        uint optimizeMemoryPriority,
        uint pagedFrozenMemoryPriority)
    {
        var fixture = CreateFixture(NativeMemoryMode.Optimize);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeProjection.Create(
                fixture.Compute,
                fixture.MemoryModes,
                fixture.ProcessFacts,
                fixture.Capabilities,
                optimizeMemoryPriority,
                pagedFrozenMemoryPriority));
    }

    private static ProjectionFixture CreateFixture(
        NativeMemoryMode firstMode,
        bool secondEligible = false)
    {
        var softwareAKey = NativeStableIdentity.CreateCaseInsensitiveKey("software-a");
        var softwareBKey = NativeStableIdentity.CreateCaseInsensitiveKey("software-b");
        var processes = new[]
        {
            CreateProcessFact(101, 1001, "software-a", "Other"),
            CreateProcessFact(202, 2002, "software-b", "Adapted")
        };
        var observedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        var processFacts = new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            InventoryGeneration,
            observedAtUtcTicks,
            2,
            2,
            0,
            0,
            SchedulingProcessMetricMask.CpuUsage,
            SchedulingProcessMetricMask.CpuUsage,
            processes)
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.CpuUsage,
                        CpuSourceGeneration + 10,
                        observedAtUtcTicks,
                        InventoryGeneration,
                        observedAtUtcTicks)
            }
        };
        var scores = ImmutableArray.Create(
            CreateProcessScore(101, 1001, softwareAKey, 0.25),
            CreateProcessScore(202, 2002, softwareBKey, 0.75),
            CreateSoftwareScore(softwareAKey, 0.25),
            CreateSoftwareScore(softwareBKey, 0.75));
        var cpu = new HostManagerComputeScoreDomainSnapshot(
                SchedulingGeneration,
                CpuSourceGeneration,
                0,
                0,
                scores);
        var compute = new HostManagerComputeScoringCycleResult(
            SchedulingGeneration,
            cpu with { SourceIdentity = cpu.SourceIdentity with { InventoryGeneration = InventoryGeneration } },
            null);
        var memoryModes = new HostManagerMemoryModeDesiredSnapshot(
            300,
            SchedulingGeneration,
            new HostManagerMemorySourceStamp(350, 400),
            SchedulingGeneration,
            false,
            firstMode == NativeMemoryMode.Optimize ? 1U : 0U,
            firstMode == NativeMemoryMode.PagedFrozen ? 1U : 0U,
            ImmutableArray.Create(
                new HostManagerDesiredMemoryMode(
                    softwareAKey,
                    SchedulingGeneration,
                    SchedulingGeneration,
                    0.25,
                    0,
                    firstMode,
                    NativeMemoryGradeSet.Known),
                new HostManagerDesiredMemoryMode(
                    softwareBKey,
                    SchedulingGeneration,
                    SchedulingGeneration,
                    0.75,
                    1,
                    NativeMemoryMode.Normal,
                    NativeMemoryGradeSet.Known)));
        var capabilities = new[]
        {
            new HostManagerNonAdaptedMemoryProcessCapability(
                101,
                1001,
                "software-a",
                true,
                false),
            new HostManagerNonAdaptedMemoryProcessCapability(
                202,
                2002,
                "software-b",
                secondEligible,
                !secondEligible)
        };
        return new ProjectionFixture(compute, memoryModes, processFacts, capabilities);
    }

    private static SchedulingProcessFact CreateProcessFact(
        int processId,
        ulong processStartKey,
        string softwareId,
        string softwareKind)
        => new(
            processId,
            processStartKey,
            $"process-{processId}",
            null,
            softwareId,
            softwareId,
            softwareKind,
            softwareKind,
            1,
            SchedulingProcessMetricMask.CpuUsage,
            1,
            0,
            InventoryGeneration,
            []);

    private static HostManagerComputeScore CreateProcessScore(
        int processId,
        ulong processStartKey,
        ulong softwareKey,
        double score)
        => new(
            NativeComputeScoringOutputKind.ProcessCpu,
            SchedulingGeneration,
            checked((ulong)processId),
            softwareKey,
            processId,
            processStartKey,
            0,
            score,
            1,
            NativeComputeScoringRuntimeState.BackgroundProcess);

    private static HostManagerComputeScore CreateSoftwareScore(
        ulong softwareKey,
        double score)
        => new(
            NativeComputeScoringOutputKind.SoftwareCpu,
            SchedulingGeneration,
            softwareKey,
            softwareKey,
            0,
            0,
            0,
            score,
            1,
            NativeComputeScoringRuntimeState.Unknown);

    private sealed record ProjectionFixture(
        HostManagerComputeScoringCycleResult Compute,
        HostManagerMemoryModeDesiredSnapshot MemoryModes,
        SchedulingProcessFactSnapshot ProcessFacts,
        IReadOnlyList<HostManagerNonAdaptedMemoryProcessCapability> Capabilities);
}
