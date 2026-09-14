using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    [Fact]
    public void Score_UsesCpuOccupancyOnlyAndSumsAllSameSoftwareProcesses()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
    public void Score_DoesNotTurnWholeAdapterUsageIntoAnotherGpuScore()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
    public void Score_AddsWelfareAndBindsCapacityValuesIntoBothSourceFingerprints()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
            2,
            facts,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), secondCapacity);

        Assert.Equal(1.875D, first!.Welfare.Share);
        Assert.Equal(3.75D, second!.Welfare.Share);
        var cpuBonus = 60D * (1 - 50D / 70);
        Assert.Equal(cpuBonus, first.Welfare.CpuBonus, precision: 10);
        Assert.Equal(first.Welfare.CpuBonus, second.Welfare.CpuBonus);
        Assert.Equal(50 + cpuBonus, Assert.Single(first.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)).Score, precision: 10);
        Assert.Equal(50 + cpuBonus, Assert.Single(second.Cpu!.Scores.Where(static score =>
            score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)).Score, precision: 10);
        Assert.Equal(50 + cpuBonus, Assert.Single(first.Memory!.Scores).Score, precision: 10);
        Assert.Equal(110, Assert.Single(second.Memory!.Scores).Score);
        Assert.NotEqual(first.Cpu.SourceFingerprint, second.Cpu.SourceFingerprint);
        Assert.NotEqual(first.Gpu!.SourceFingerprint, second.Gpu!.SourceFingerprint);
    }

    [Fact]
    public void Score_BindsChangedSoftwareBaseMeanIntoSourceFingerprint()
    {
        var firstConfiguration = CreateConfiguration();
        var secondConfiguration = CreateConfiguration();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            firstConfiguration.CpuBaselineRatio);
        using var firstWorkspace = new HostManagerComputeScoringWorkspace(
            in firstConfiguration,
            cpuScoring);
        using var secondWorkspace = new HostManagerComputeScoringWorkspace(
            in secondConfiguration,
            cpuScoring);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, firstMemoryPercent: 10);
        var capacity = new HostManagerWelfareCapacityInput(0.5, 0.5, 0.5, 0.5, 2);

        var first = firstWorkspace.Score(
            1,
            facts,
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), capacity);
        var second = secondWorkspace.Score(
            1,
            facts with { SoftwareBaseScores = [new("software", 30)] },
            inventory,
            CreateRuntimeFacts(), CreateCpuResidency(), capacity);

        Assert.Equal(1.875D, first!.Welfare.Share);
        Assert.Equal(0.9375D, second!.Welfare.Share);
        Assert.NotEqual(first.Cpu!.SourceFingerprint, second.Cpu!.SourceFingerprint);
        Assert.NotEqual(first.Gpu!.SourceFingerprint, second.Gpu!.SourceFingerprint);
    }

    [Fact]
    public void Score_BindsNewWelfarePublicationEvenWhenItsValueIsUnchanged()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(
            in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(
                HostManagerTestPlanFactory.CreateCpuTopology(1),
                configuration.CpuBaselineRatio));
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, firstMemoryPercent: 10);
        var firstCapacity = new HostManagerWelfareCapacityInput(
            0.5, 0.5, 0.5, 0.5, 2, PublicationFingerprint: 101);
        var secondCapacity = firstCapacity with { PublicationFingerprint = 102 };

        var first = workspace.Score(
            1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), firstCapacity);
        var second = workspace.Score(
            2, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), secondCapacity);

        Assert.Equal(first!.Cpu!.Scores.Select(static score => score.Score),
            second!.Cpu!.Scores.Select(static score => score.Score));
        Assert.Equal(first.Gpu!.Scores.Select(static score => score.Score),
            second.Gpu!.Scores.Select(static score => score.Score));
        Assert.NotEqual(first.Cpu.SourceFingerprint, second.Cpu.SourceFingerprint);
        Assert.NotEqual(first.Gpu.SourceFingerprint, second.Gpu.SourceFingerprint);
    }

    [Fact]
    public void Score_BindsWelfareCutoffEvenWhenCurrentScoresAreEqual()
    {
        var firstConfig = CreateConfiguration();
        var secondConfig = firstConfig;
        secondConfig.WelfareUtilizationBaselinePercent = 100;
        var plan = HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1));
        using var firstWorkspace = new HostManagerComputeScoringWorkspace(in firstConfig, plan);
        using var secondWorkspace = new HostManagerComputeScoringWorkspace(in secondConfig, plan);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var capacity = new HostManagerWelfareCapacityInput(0, 0, 0, 0, 2);

        var first = firstWorkspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), capacity)!;
        var second = secondWorkspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), capacity)!;

        Assert.Equal(first.Cpu!.Scores.Select(static row => row.Score), second.Cpu!.Scores.Select(static row => row.Score));
        Assert.Equal(Assert.Single(first.Memory!.Scores).Score, Assert.Single(second.Memory!.Scores).Score);
        Assert.NotEqual(first.Cpu.SourceFingerprint, second.Cpu.SourceFingerprint);
        Assert.NotEqual(first.Memory.SourceFingerprint, second.Memory.SourceFingerprint);
    }

    [Fact]
    public void SoftwareMeanSurvivesProcessCountStateAndLoadChangesButTracksCatalogEdits()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10) with
        {
            SoftwareBaseScores = [new("software", 80), new("not-running-software", 40)]
        };
        var capacity = new HostManagerWelfareCapacityInput(0.5, 1, 1, 1, 2);
        var first = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), capacity)!;
        Assert.Equal(60, first.Welfare.SoftwareBaseMean);
        Assert.Equal(30, first.Welfare.Budget);
        Assert.Equal(15, first.Welfare.Share);
        Assert.Equal(60D * (1 - 50D / 70), first.Welfare.CpuBonus, precision: 10);
        Assert.Equal(60, first.Welfare.MemoryBonus);

        var one = facts with { EnumeratedCount = 1, EmittedCount = 1, Processes = [facts.Processes[0]] };
        var runtime = CreateRuntimeFacts().Where(pair => pair.Key.ProcessId == 10)
            .ToDictionary();
        var singleCapacity = capacity with { EligibleProcessCount = 1 };
        var second = workspace.Score(2, one, inventory, runtime, CreateCpuResidency(), singleCapacity)!;
        Assert.Equal(first.Welfare.SoftwareBaseMean, second.Welfare.SoftwareBaseMean);
        Assert.Equal(30, second.Welfare.Share);
        Assert.Equal(first.Welfare.Share * 2, second.Welfare.Share);
        Assert.Equal(first.Welfare.CpuBonus, second.Welfare.CpuBonus);
        Assert.Equal(first.Welfare.MemoryBonus, second.Welfare.MemoryBonus);
        Assert.Equal(Assert.Single(first.Cpu!.Scores, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score,
            Assert.Single(second.Cpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score);

        var changedProcess = one with { Processes = [one.Processes[0] with { BaseScore = 1, CpuUsagePercent = 99 }] };
        runtime[new(10, 100)] = new(NativeComputeScoringRuntimeState.BackgroundProcess, 0.25, 0.5);
        var third = workspace.Score(3, changedProcess, inventory, runtime, CreateCpuResidency(), singleCapacity)!;
        Assert.Equal(second.Welfare, third.Welfare);
        Assert.NotEqual(second.Cpu!.Scores[0].Score, third.Cpu!.Scores[0].Score);

        ulong generation = 4;
        foreach (var (catalog, mean) in new (SoftwareBaseScore[], double)[]
        {
            ([new("software", 20), new("not-running-software", 40)], 30),
            ([new("not-running-software", 40)], 40),
            ([], 0),
            ([new("software", 80), new("not-running-software", 40)], 60)
        })
        {
            var changed = workspace.Score(generation++, one with { SoftwareBaseScores = [.. catalog] },
                inventory, runtime, CreateCpuResidency(), singleCapacity)!;
            Assert.Equal(mean, changed.Welfare.SoftwareBaseMean);
            Assert.Equal(mean * 0.5, changed.Welfare.Share);
            Assert.Equal(mean * (1 - 50D / 70), changed.Welfare.CpuBonus, precision: 10);
            Assert.Equal(mean, changed.Welfare.MemoryBonus);
        }
    }

    [Fact]
    public void Score_ComposesIndependentProductionPublicationsIntoCompleteFingerprint()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
        Assert.Equal(source.Generation, cpu.SourceIdentity.InventoryGeneration);
        Assert.Equal(attributionGeneration, cpu.SourceIdentity.AttributionGeneration);
        Assert.Equal(runtimeGeneration, cpu.SourceIdentity.RuntimeStateGeneration);
        Assert.Equal(71UL, cpu.SourceIdentity.MetricGeneration);
        Assert.Equal(CreateCpuResidency().CapturedAt.UtcTicks, cpu.SourceIdentity.MetricObservedAtUtcTicks);
        Assert.NotEqual(
            cpu.SourceIdentity.InventoryGeneration,
            runtimeInventoryGeneration);
        Assert.NotEqual(
            cpu.SourceIdentity.InventoryGeneration,
            cpuInventoryGeneration);
        Assert.NotEqual(0UL, cpu.SourceIdentity.SourceFingerprint);
        Assert.Equal(2, cpu.Scores.Count(static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu));
    }

    [Fact]
    public void Score_UsesOnlyProcessesAndAdaptersObservedInTheGpuDomain()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
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

    [Fact]
    public void ShadowMergeDoesNotCombineDomainsFromDifferentAttempts()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1), configuration.CpuBaselineRatio));
        var inventory = CreateInventory();
        var fullFacts = CreateFacts(inventory, firstMemoryPercent: 10);
        var full = workspace.Score(1, fullFacts, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        Assert.NotNull(full?.Cpu);
        Assert.NotNull(full?.Gpu);

        var nextGeneration = fullFacts.Generation + 1;
        var cpuOnlyFacts = fullFacts with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage,
            Processes = fullFacts.Processes.Select(process => process with
            {
                ValidMetricMask = SchedulingProcessMetricMask.CpuUsage,
                SourceGeneration = nextGeneration,
                Gpus = []
            }).ToArray(),
            Generation = nextGeneration,
            GpuSourceGeneration = 0,
            GpuObservedAtUtcTicks = 0,
            GpuTopologyGeneration = 0,
            GpuTopologyFingerprint = 0
        };
        var cpuOnly = workspace.Score(2, cpuOnlyFacts, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        Assert.NotNull(cpuOnly?.Cpu);
        Assert.Null(cpuOnly?.Gpu);

        var shadow = HostManagerComputeScoringShadowSnapshot.Empty.Merge(full!);
        shadow = shadow.Merge(cpuOnly!);
        Assert.Equal(2UL, shadow.LastAttemptGeneration);
        Assert.Equal(2UL, shadow.Cpu?.SchedulingGeneration);
        Assert.Null(shadow.Gpu);
    }

    private static ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot CreateCpuResidency()
        => CpuCoreResidencyTestValues.Create(71, new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero),
            CpuCoreResidencyTestValues.Process(10, 100, ("core:0", 50)),
            CpuCoreResidencyTestValues.Process(20, 200, ("core:0", 0)));

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
            welfareUtilizationBaselinePercent: 70);
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        CreateRuntimeFacts()
        => new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        {
            [new HostManagerComputeProcessIdentity(10, 100)] = new(
                NativeComputeScoringRuntimeState.Unknown,
                1,
                1),
            [new HostManagerComputeProcessIdentity(20, 200)] = new(
                NativeComputeScoringRuntimeState.Unknown,
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
                CreateProcess(10, 100, 50, firstMemoryPercent, 25, 50),
                CreateProcess(20, 200, 0, 1, 25, 0)
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
                        now,
                        memoryUsageDependency:
                            TestSystemMemoryUsageDependency.Create(
                                sourceGeneration: 101,
                                observedAtUtcTicks: now,
                                committedGeneration: 101)),
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
            "Other",
            "Other",
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
