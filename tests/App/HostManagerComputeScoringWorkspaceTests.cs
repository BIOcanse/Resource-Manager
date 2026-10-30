using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    private const ulong FirstStartKey = 133_900_000_000_000_000;
    private const ulong SecondStartKey = FirstStartKey + 10_000_000;

    [Fact]
    public void GpuScoringProjectionDoesNotRetainMemoryValuesWithoutTheirSource()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        facts = facts with { Processes = facts.Processes.Select(p => p with
        {
            Gpus = p.Gpus.Select(g => g with { PrivateMemoryBytes = 1024, SharedMemoryBytes = 512 }).ToArray()
        }).ToArray() };
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), null);
        Assert.NotNull(result?.Gpu);
        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, result.CpuOccupancySource);
        Assert.All(facts.Processes.SelectMany(p => p.Gpus), g => Assert.Equal(1536, g.ResidentMemoryBytes));
    }

    [Fact]
    public void Score_UsesCpuOccupancyOnlyAndSumsAllSameSoftwareProcesses()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var first = workspace.Score(
            1,
            CreateFacts(inventory, firstMemoryPercent: 10),
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());
        var second = workspace.Score(
            2,
            CreateFacts(inventory, firstMemoryPercent: 99),
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        Assert.NotNull(first?.Cpu);
        Assert.NotNull(second?.Cpu);
        var firstSoftware = Assert.Single(first!.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu));
        var secondSoftware = Assert.Single(second!.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu));
        Assert.Equal(50D, firstSoftware.Score);
        Assert.Equal(firstSoftware.Score, secondSoftware.Score);
        Assert.Equal(2U, firstSoftware.MemberCount);
        var zeroProcess = Assert.Single(first.Cpu.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu
                && score.ProcessId == 20));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(zeroProcess.Score));
    }

    [Fact]
    public void Score_KeepsStableGpuAdaptersIndependent()
    {
        var configuration = CreateConfiguration(cpuBaselineRatio: 0.5);
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();

        var result = workspace.Score(
            1,
            CreateFacts(inventory, firstMemoryPercent: 10),
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        Assert.NotNull(result?.Gpu);
        Assert.Equal(100D, Assert.Single(result!.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)).Score);
        var software = result!.Gpu!.Scores
            .Where(static score => score.Kind == NativeComputeScoringOutputKind.SoftwareGpu)
            .OrderBy(static score => score.AdapterKey)
            .ToArray();
        Assert.Equal(2, software.Length);
        Assert.Equal(11UL, software[0].AdapterKey);
        Assert.Equal(50D, software[0].Score);
        Assert.Equal(22UL, software[1].AdapterKey);
        Assert.Equal(50D, software[1].Score);
    }

    [Fact]
    public void Score_NarrowNativeRowsGiveConsumersTheIdentityAndScoresOfTheRowsTheyName()
    {
        var configuration = CreateConfiguration(cpuBaselineRatio: 0.5);
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();

        var result = workspace.Score(
            1,
            CreateFacts(inventory, firstMemoryPercent: 10),
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        Assert.NotNull(result?.Cpu);
        Assert.NotNull(result!.Gpu);
        Assert.NotNull(result.Memory);
        var rows = result.Cpu!.Scores.Concat(result.Gpu!.Scores).Concat(result.Memory!.Scores).ToArray();
        Assert.NotEqual(0UL, Assert.Single(rows.Select(static row => row.SoftwareKey).Distinct()));
        Assert.All(rows, static row => Assert.Equal(1UL, row.RoundNumber));
        // A process row carries the identity and runtime state of the process input it names.
        var processRows = rows.Where(static row => row.Kind is NativeComputeScoringOutputKind.ProcessCpu
            or NativeComputeScoringOutputKind.ProcessGpu).ToArray();
        Assert.All(processRows, static row =>
        {
            Assert.Contains(row.ProcessId, new[] { 10, 20 });
            Assert.Equal(row.ProcessId == 10 ? FirstStartKey : SecondStartKey, row.ProcessStartKey);
            Assert.Equal(NativeComputeScoringRuntimeState.BackgroundProcess, row.RuntimeState);
            Assert.Equal(1U, row.MemberCount);
        });
        Assert.All(processRows.GroupBy(static row => row.ProcessId),
            static process => Assert.Single(process.Select(static row => row.TargetKey).Distinct()));
        // A software row carries only its software and, for GPU, its adapter.
        Assert.All(rows.Where(static row => row.Kind is NativeComputeScoringOutputKind.SoftwareCpu
            or NativeComputeScoringOutputKind.SoftwareGpu or NativeComputeScoringOutputKind.SoftwareMemory), static row =>
        {
            Assert.Equal((0UL, 0, 0UL, NativeComputeScoringRuntimeState.Unknown),
                (row.TargetKey, row.ProcessId, row.ProcessStartKey, row.RuntimeState));
            Assert.NotEqual(0U, row.MemberCount);
        });
        // The scores this fixture already pins, each found under the identity it had before.
        Assert.Equal(100D, Row(NativeComputeScoringOutputKind.ProcessCpu, 10, 0).Score);
        Assert.Equal(0D, Row(NativeComputeScoringOutputKind.ProcessCpu, 20, 0).Score);
        var softwareCpu = Row(NativeComputeScoringOutputKind.SoftwareCpu, 0, 0);
        Assert.Equal((100D, 2U), (softwareCpu.Score, softwareCpu.MemberCount));
        Assert.Equal(25D, Row(NativeComputeScoringOutputKind.ProcessGpu, 10, 11).Score);
        Assert.Equal(25D, Row(NativeComputeScoringOutputKind.ProcessGpu, 20, 11).Score);
        Assert.Equal(50D, Row(NativeComputeScoringOutputKind.ProcessGpu, 10, 22).Score);
        Assert.Equal(50D, Row(NativeComputeScoringOutputKind.SoftwareGpu, 0, 11).Score);
        Assert.Equal(50D, Row(NativeComputeScoringOutputKind.SoftwareGpu, 0, 22).Score);
        Row(NativeComputeScoringOutputKind.SoftwareMemory, 0, 0);

        HostManagerComputeScore Row(NativeComputeScoringOutputKind kind, int processId, ulong adapterKey)
            => Assert.Single(rows, row => row.Kind == kind && row.ProcessId == processId && row.AdapterKey == adapterKey);
    }

    [Fact]
    public void Score_DoesNotTurnWholeAdapterUsageIntoAnotherGpuScore()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var idleInventory = CreateInventory();
        var saturatedInventory = idleInventory with
        {
            Adapters = idleInventory.Adapters
                .Select(static adapter => adapter with { UsagePercent = 99 })
                .ToArray()
        };
        var facts = CreateFacts(idleInventory, firstMemoryPercent: 10);

        var idle = workspace.Score(
            1,
            facts,
            idleInventory,
            CreateRuntimeFacts(), CreateCpuResidency());
        var saturated = workspace.Score(
            2,
            facts,
            saturatedInventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        var idleScores = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(idle?.Gpu)
            .Scores
            .Select(static score => (score.Kind, score.ProcessId, score.AdapterKey, score.Score, score.MemberCount))
            .ToArray();
        var saturatedScores = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(saturated?.Gpu)
            .Scores
            .Select(static score => (score.Kind, score.ProcessId, score.AdapterKey, score.Score, score.MemberCount))
            .ToArray();
        Assert.Equal(idleScores, saturatedScores);
    }

    [Fact]
    public void Score_RecomputesWelfareWhenCapacityChangesInTheSameRound()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, firstMemoryPercent: 10);
        var firstCapacity = new HostManagerWelfareCapacityInput(0.5, 0.5, 0.5, 0.5, 2);
        var secondCapacity = firstCapacity with { MemoryFreeRatio = 1 };

        var first = workspace.Score(
            1,
            facts,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), firstCapacity);
        var second = workspace.Score(
            1,
            facts,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), secondCapacity);

        Assert.Equal(1.875D, first!.Welfare.Share);
        Assert.Equal(3.75D, second!.Welfare.Share);
        var cpuBonus = 60D * 0.5;
        Assert.Equal(cpuBonus, first.Welfare.CpuBonus, precision: 10);
        Assert.Equal(first.Welfare.CpuBonus, second.Welfare.CpuBonus);
        Assert.Equal(50 + cpuBonus, Assert.Single(first.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)).Score, precision: 10);
        Assert.Equal(50 + cpuBonus, Assert.Single(second.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)).Score, precision: 10);
        Assert.Equal(50 + cpuBonus, Assert.Single(first.Memory!.Scores).Score, precision: 10);
        Assert.Equal(110, Assert.Single(second.Memory!.Scores).Score);
    }

    [Fact]
    public void Score_CatalogChangesDoNotChangeWelfare()
    {
        var configuration = CreateConfiguration();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            configuration.CpuBaselineRatio);
        using var workspace = new HostManagerComputeScoringWorkspace(
            in configuration,
            cpuScoring, CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, firstMemoryPercent: 10);
        var capacity = new HostManagerWelfareCapacityInput(0.5, 0.5, 0.5, 0.5, 2);

        var first = workspace.Score(
            1,
            facts,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), capacity);
        var second = workspace.Score(
            1,
            facts with { SoftwareBaseScores = [new("software", 30)] },
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), capacity);

        Assert.Equal(1.875D, first!.Welfare.Share);
        Assert.Equal(first.Welfare, second!.Welfare);
    }

    [Fact]
    public void Score_RecomputesTheSameRoundWithIdenticalResults()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(
            in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(
                HostManagerTestPlanFactory.CreateCpuTopology(1),
                configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, firstMemoryPercent: 10);
        var firstCapacity = new HostManagerWelfareCapacityInput(
            0.5, 0.5, 0.5, 0.5, 2);
        var secondCapacity = firstCapacity;

        var first = workspace.Score(
            1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), firstCapacity);
        var second = workspace.Score(
            1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), secondCapacity);

        Assert.Equal(1UL, second!.RoundNumber);
        Assert.Equal<HostManagerComputeScore>(first!.Cpu!.Scores, second.Cpu!.Scores);
        Assert.Equal<HostManagerComputeScore>(first.Gpu!.Scores, second.Gpu!.Scores);
        Assert.Equal<HostManagerComputeScore>(first.Memory!.Scores, second.Memory!.Scores);
        Assert.Equal(first.Welfare, second.Welfare);
    }

    [Fact]
    public void WelfareBaseIsIndependentOfCatalogProcessCountStateAndLoad()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10) with
        {
            SoftwareBaseScores = [new("software", 80), new("not-running-software", 40)]
        };
        var capacity = new HostManagerWelfareCapacityInput(0.5, 1, 1, 1, 2);
        var first = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), capacity)!;
        Assert.Equal(60, first.Welfare.WelfareBaseScore);
        Assert.Equal(30, first.Welfare.Budget);
        Assert.Equal(15, first.Welfare.Share);
        Assert.Equal(60D * 0.5, first.Welfare.CpuBonus, precision: 10);
        Assert.Equal(60, first.Welfare.MemoryBonus);

        var one = facts with { EnumeratedCount = 1, EmittedCount = 1, Processes = [facts.Processes[0]] };
        var runtime = CreateRuntimeFacts().Where(pair => pair.Key.ProcessId == 10)
            .ToDictionary();
        var singleCapacity = capacity with { EligibleProcessCount = 1 };
        var second = workspace.Score(2, one, inventory, runtime, CreateCpuResidency(), singleCapacity)!;
        Assert.Equal(first.Welfare.WelfareBaseScore, second.Welfare.WelfareBaseScore);
        Assert.Equal(30, second.Welfare.Share);
        Assert.Equal(first.Welfare.Share * 2, second.Welfare.Share);
        Assert.Equal(first.Welfare.CpuBonus, second.Welfare.CpuBonus);
        Assert.Equal(first.Welfare.MemoryBonus, second.Welfare.MemoryBonus);
        Assert.Equal(Assert.Single(first.Cpu!.Scores, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score,
            Assert.Single(second.Cpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score);

        var changedProcess = one with { Processes = [one.Processes[0] with { BaseScore = 1, CpuUsagePercent = 99 }] };
        runtime[new(10, FirstStartKey)] = new(NativeComputeScoringRuntimeState.BackgroundProcess, 0.25, 0.5);
        var third = workspace.Score(3, changedProcess, inventory, runtime, CreateCpuResidency(), singleCapacity)!;
        Assert.Equal(second.Welfare, third.Welfare);
        Assert.NotEqual(second.Cpu!.Scores[0].Score, third.Cpu!.Scores[0].Score);

        ulong generation = 4;
        foreach (var catalog in new SoftwareBaseScore[][]
        {
            [new("software", 20), new("not-running-software", 40)],
            [new("not-running-software", 40)],
            [],
            [new("software", 80), new("not-running-software", 40)]
        })
        {
            var changed = workspace.Score(generation++, one with { SoftwareBaseScores = [.. catalog] },
                inventory, runtime, CreateCpuResidency(), singleCapacity)!;
            Assert.Equal(60, changed.Welfare.WelfareBaseScore);
            Assert.Equal(60 * 0.5, changed.Welfare.Share);
            Assert.Equal(60 * 0.5, changed.Welfare.CpuBonus, precision: 10);
            Assert.Equal(60, changed.Welfare.MemoryBonus);
        }
    }

    [Fact]
    public void Score_ComposesIndependentProductionPublicationsIntoOneRound()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var source = CreateFacts(inventory, firstMemoryPercent: 10);
        var attributionGeneration = checked(source.Generation + 11);
        var runtimeGeneration = checked(source.Generation + 21);
        var runtimeInventoryGeneration = checked(source.Generation + 31);
        var cpuGeneration = checked(source.Generation + 41);
        var cpuInventoryGeneration = checked(source.Generation + 51);
        var observations = source.DatasetObservations.ToDictionary();
        observations[SchedulingProcessMetricMask.CpuUsage] =
            SchedulingProcessDatasetObservation.CreateCurrent(
                SchedulingProcessMetricMask.CpuUsage,
                cpuGeneration,
                checked(source.ObservedAtUtcTicks + 41),
                cpuInventoryGeneration,
                checked(source.ObservedAtUtcTicks + 51));
        observations[SchedulingProcessMetricMask.RuntimeState] =
            SchedulingProcessDatasetObservation.CreateCurrent(
                SchedulingProcessMetricMask.RuntimeState,
                runtimeGeneration,
                checked(source.ObservedAtUtcTicks + 21),
                runtimeInventoryGeneration,
                checked(source.ObservedAtUtcTicks + 31));
        var production = source with
        {
            RequestedMetricMask = source.RequestedMetricMask
                | SchedulingProcessMetricMask.RuntimeState,
            CurrentMetricMask = source.CurrentMetricMask
                | SchedulingProcessMetricMask.RuntimeState,
            Processes = source.Processes.Select(static process => process with
            {
                ValidMetricMask = process.ValidMetricMask
                    | SchedulingProcessMetricMask.RuntimeState,
                RuntimeState = HostManagerRuntimeStates.BackgroundProcess
            }).ToArray(),
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = source.Processes,
            AttributionProcesses = source.Processes,
            FoundationDatasetObservations = new Dictionary<
                string,
                SchedulingProcessFoundationDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.ProcessInventory] =
                    SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                        SamplingDatasetIds.ProcessInventory,
                        source.Generation,
                        source.ObservedAtUtcTicks),
                [SamplingDatasetIds.ProcessAttribution] =
                    SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                        SamplingDatasetIds.ProcessAttribution,
                        attributionGeneration,
                        checked(source.ObservedAtUtcTicks + 11))
            },
            DatasetObservations = observations
        };

        var result = workspace.Score(
            1,
            production,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        var cpu = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(result?.Cpu);
        Assert.Equal(1UL, cpu.RoundNumber);
        Assert.All(cpu.Scores, static score => Assert.Equal(1UL, score.RoundNumber));
        Assert.Equal(2, cpu.Scores.Count(static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu));
    }

    [Fact]
    public void Score_UsesOnlyProcessesAndAdaptersObservedInTheGpuDomain()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var full = CreateFacts(inventory, firstMemoryPercent: 10);
        var sparse = full with
        {
            Processes =
            [
                full.Processes[0] with
                {
                    Gpus =
                    [
                        full.Processes[0].Gpus[0] with
                        {
                            ValidMetricMask = SchedulingProcessMetricMask.GpuUsage,
                            DedicatedMemoryUsedPercent = 0,
                            DedicatedMemorySourceGeneration = 0,
                            DedicatedMemoryTopologyGeneration = 0
                        }
                    ]
                },
                full.Processes[1] with
                {
                    ValidMetricMask = SchedulingProcessMetricMask.CpuUsage
                        | SchedulingProcessMetricMask.MemoryUsage,
                    Gpus = []
                }
            ]
        };

        var result = workspace.Score(
            1,
            sparse,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        Assert.Equal(2, result!.Cpu!.Scores.Count(static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu));
        var gpu = Assert.Single(result.Gpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessGpu));
        Assert.Equal(10, gpu.ProcessId);
        Assert.Equal(11UL, gpu.AdapterKey);
        Assert.DoesNotContain(result.Gpu.Scores, static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessGpu
                && score.ProcessId == 20);
    }

    [Fact]
    public void Score_PublishesOnlyTheCompleteDomain()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio), CreateScratchFixture);
        var inventory = CreateInventory();
        var full = CreateFacts(inventory, firstMemoryPercent: 10);
        var cpuOnly = full with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage,
            Processes = full.Processes.Select(static process => process with
            {
                ValidMetricMask = SchedulingProcessMetricMask.CpuUsage,
                Gpus = []
            }).ToArray(),
            GpuSourceGeneration = 0,
            GpuObservedAtUtcTicks = 0,
            GpuTopologyGeneration = 0,
            GpuTopologyFingerprint = 0
        };

        var result = workspace.Score(
            1,
            cpuOnly,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency());

        Assert.NotNull(result?.Cpu);
        Assert.Null(result!.Gpu);
    }


    private static ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot CreateCpuResidency()
        => CpuCoreResidencyTestValues.Create(71, new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero),
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 50)),
            CpuCoreResidencyTestValues.Process(20, SecondStartKey, ("core:0", 0)));

    private static NativeComputeScoringConfiguration CreateConfiguration(
        double cpuBaselineRatio = 1,
        uint cpuCoreCount = 1)
    {
        ReadOnlySpan<double> multipliers = [1, 1, 1, 1, 1, 1, 0];
        return NativeComputeScoringConfigurationWriter.Create(
            1,
            8,
            16,
            56,
            multipliers,
            multipliers,
            100,
            1,
            cpuBaselineRatio,
            cpuCoreCount,
            welfareBaseScore: 60);
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        CreateRuntimeFacts()
        => new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        {
            [new HostManagerComputeProcessIdentity(10, FirstStartKey)] = new(
                NativeComputeScoringRuntimeState.BackgroundProcess,
                1,
                1),
            [new HostManagerComputeProcessIdentity(20, SecondStartKey)] = new(
                NativeComputeScoringRuntimeState.BackgroundProcess,
                1,
                1)
        };

    private static SchedulingGpuInventorySnapshot CreateInventory()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        return new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.Current,
            9,
            now,
            2,
            0,
            0,
            303,
            [
                CreateAdapter(0, 11, now),
                CreateAdapter(1, 22, now)
            ]);
    }

    private static SchedulingGpuAdapterObservation CreateAdapter(
        int index,
        ulong adapterKey,
        long observedAtUtcTicks)
        => new(
            index,
            adapterKey,
            SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
            SchedulingGpuMetricMask.Usage |
                SchedulingGpuMetricMask.UsedDedicatedMemory |
                SchedulingGpuMetricMask.TotalDedicatedMemory,
            SamplingObservationStatus.Current,
            SamplingObservationStatus.Current,
            0,
            0,
            8UL * 1024 * 1024 * 1024,
            9,
            observedAtUtcTicks);

    private static SchedulingProcessFactSnapshot CreateFacts(
        SchedulingGpuInventorySnapshot inventory,
        double firstMemoryPercent)
    {
        const SchedulingProcessMetricMask all = SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.GpuUsage |
            SchedulingProcessMetricMask.GpuDedicatedMemory;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            101,
            now,
            2,
            2,
            0,
            0,
            all,
            all,
            [
                CreateProcess(10, FirstStartKey, 50, firstMemoryPercent, 25, 50),
                CreateProcess(20, SecondStartKey, 0, 1, 25, 0)
            ],
            202,
            now,
            inventory.Generation,
            inventory.TopologyFingerprint)
        {
            SoftwareBaseScores = [new("software", 60)],
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.CpuUsage,
                        101,
                        now,
                        101,
                        now),
                [SchedulingProcessMetricMask.MemoryUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.MemoryUsage,
                        101,
                        now,
                        101,
                        now),
                [SchedulingProcessMetricMask.GpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.GpuUsage,
                        202,
                        now,
                        101,
                        now,
                        topologyGeneration: inventory.Generation,
                        topologyFingerprint: inventory.TopologyFingerprint),
                [SchedulingProcessMetricMask.GpuDedicatedMemory] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.GpuDedicatedMemory,
                        202,
                        now,
                        101,
                        now,
                        topologyGeneration: inventory.Generation,
                        topologyFingerprint: inventory.TopologyFingerprint)
            }
        };
    }

    private static SchedulingProcessFact CreateProcess(
        int processId,
        ulong startKey,
        double cpu,
        double memory,
        double gpu11,
        double gpu22)
    {
        const SchedulingProcessMetricMask all = SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.GpuUsage |
            SchedulingProcessMetricMask.GpuDedicatedMemory;
        return new SchedulingProcessFact(
            processId,
            startKey,
            $"process-{processId}",
            null,
            "software",
            "Software",
            "other",
            "other",
            100,
            all,
            cpu,
            memory,
            101,
            [
                new SchedulingProcessGpuFact(0, 11, all, gpu11, 0, 202, 202, 9, 9),
                new SchedulingProcessGpuFact(1, 22, all, gpu22, 0, 202, 202, 9, 9)
            ]);
    }
}
