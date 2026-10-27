using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(9007199254740995UL)]
    public void InvalidGpuIdentityPreservesObservedAdapterForEveryGpuFact(ulong adapterKey)
    {
        var inventory = CreateInventory();
        var source = CreateFacts(inventory, 10);
        var process = source.Processes[0];
        var gpu = process.Gpus[0] with { AdapterKey = adapterKey, PrivateMemoryBytes = 1024, SharedMemoryBytes = 512 };
        source = source with { Processes = [process with { Gpus = [gpu] }, source.Processes[1]] };

        var projected = source.ProjectValidFacts(inventory);
        var exclusions = projected.FactExclusions.Where(e => e.ProcessId == process.ProcessId
            && e.Reason == SchedulingFactExclusionReason.GpuRowMissingOrInvalid).ToArray();
        Assert.Equal(new[] { SchedulingFactCode.GpuUsage, SchedulingFactCode.GpuDedicatedMemory,
            SchedulingFactCode.GpuPrivateMemoryBytes, SchedulingFactCode.GpuSharedMemoryBytes }, exclusions.Select(e => e.Fact));
        Assert.All(exclusions, e => Assert.Equal(adapterKey, e.AdapterKey));
    }

    [Theory]
    [InlineData(SchedulingFactCode.GpuUsage)]
    [InlineData(SchedulingFactCode.GpuDedicatedMemory)]
    public void InvalidGpuMetricEmitsFactCodeAndSeparateAdapter(SchedulingFactCode fact)
    {
        var inventory = CreateInventory();
        var source = CreateFacts(inventory, 10);
        var process = source.Processes[0];
        var gpu = process.Gpus[0] with
        {
            UsagePercent = fact == SchedulingFactCode.GpuUsage ? double.NaN : 25,
            DedicatedMemoryUsedPercent = fact == SchedulingFactCode.GpuDedicatedMemory ? double.NaN : 42
        };
        source = source with { Processes = [process with { Gpus = [gpu] }, source.Processes[1]] };
        var projected = source.ProjectValidFacts(inventory);
        Assert.Contains(projected.FactExclusions, exclusion => exclusion.ProcessId == process.ProcessId
            && exclusion.Fact == fact && exclusion.AdapterKey == gpu.AdapterKey
            && exclusion.Reason == SchedulingFactExclusionReason.GpuRowMissingOrInvalid);
        Assert.All(projected.FactExclusions, exclusion =>
        {
            Assert.True(Enum.IsDefined(exclusion.Fact));
            Assert.True(Enum.IsDefined(exclusion.Reason));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidResidencyByteKeepsOtherByteAndValidGpuPercentages(bool invalidPrivate)
    {
        var inventory = CreateInventory();
        var source = CreateFacts(inventory, 10);
        var first = source.Processes[0];
        var gpu = first.Gpus[0] with
        {
            UsagePercent = 25,
            DedicatedMemoryUsedPercent = 42,
            PrivateMemoryBytes = invalidPrivate ? double.NaN : 1024,
            SharedMemoryBytes = invalidPrivate ? 512 : double.NaN
        };
        source = source with { Processes = [first with { Gpus = [gpu] }, source.Processes[1]] };

        var projected = source.ProjectValidFacts(inventory);
        var accepted = Assert.Single(projected.Processes[0].Gpus);
        Assert.Equal(25, accepted.UsagePercent);
        Assert.Equal(42, accepted.DedicatedMemoryUsedPercent);
        Assert.True(accepted.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage));
        Assert.True(accepted.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory));
        Assert.Equal(invalidPrivate ? null : 1024d, accepted.PrivateMemoryBytes);
        Assert.Equal(invalidPrivate ? 512d : null, accepted.SharedMemoryBytes);
        Assert.Null(accepted.ResidentMemoryBytes);
        Assert.Contains(projected.FactExclusions, exclusion => exclusion.Fact ==
            (invalidPrivate ? SchedulingFactCode.GpuPrivateMemoryBytes : SchedulingFactCode.GpuSharedMemoryBytes) && exclusion.AdapterKey == 11);
        Assert.Equal(accepted, Assert.Single(projected.ProjectValidFacts(inventory).Processes[0].Gpus));
    }

    [Fact]
    public void UnavailableDedicatedCapacityOmitsOnlyPercentageAndKeepsResidencyAndUsage()
    {
        var inventory = CreateInventory();
        inventory = inventory with { Adapters = inventory.Adapters.Select(adapter => adapter with
            { CapacityStatus = ResourceManager.App.Domain.Metrics.SamplingObservationStatus.Unavailable }).ToArray() };
        var source = CreateFacts(inventory, 10);
        source = source with { Processes = source.Processes.Select(process => process with
        {
            Gpus = process.Gpus.Select(gpu => gpu with { PrivateMemoryBytes = 1024, SharedMemoryBytes = 512 }).ToArray()
        }).ToArray() };

        var projected = source.ProjectValidFacts(inventory);

        Assert.All(projected.Processes.SelectMany(process => process.Gpus), gpu =>
        {
            Assert.True(gpu.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage));
            Assert.False(gpu.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory));
            Assert.Equal(1536, gpu.ResidentMemoryBytes);
        });
    }

    [Fact]
    public void CleanupFactIndexOmitsDuplicateAndInvalidMemoryProcessesIndividually()
    {
        var inventory = CreateInventory();
        var source = CreateFacts(inventory, 10);
        var runtimeObservation = source.DatasetObservations[SchedulingProcessMetricMask.CpuUsage];
        var observations = source.DatasetObservations.ToDictionary();
        observations[SchedulingProcessMetricMask.RuntimeState] = SchedulingProcessDatasetObservation.CreateCurrent(
            SchedulingProcessMetricMask.RuntimeState, runtimeObservation.SourceGeneration,
            runtimeObservation.ObservedAtUtcTicks, runtimeObservation.InventoryGeneration,
            runtimeObservation.InventoryObservedAtUtcTicks);
        var first = source.Processes[0] with
            { ValidMetricMask = source.Processes[0].ValidMetricMask | SchedulingProcessMetricMask.RuntimeState,
                RuntimeState = ResourceManager.App.Domain.Optimization.HostManagerRuntimeStates.BackgroundProcess };
        var second = first with { ProcessId = 20, ProcessStartKey = SecondStartKey, MemoryUsedBytes = double.NaN };
        var third = first with { ProcessId = 30, ProcessStartKey = 300 };
        source = source with
        {
            RequestedMetricMask = source.RequestedMetricMask | SchedulingProcessMetricMask.RuntimeState,
            CurrentMetricMask = source.CurrentMetricMask | SchedulingProcessMetricMask.RuntimeState,
            Processes = [first, first, second, third], DatasetObservations = observations,
            EnumeratedCount = 4, EmittedCount = 4
        };
        object?[] arguments = [source, 1024UL, null, null];

        var accepted = (bool)typeof(HostManagerSmartCoordinator).GetMethod("TryIndexCurrentMemoryCleanupProcessFacts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, arguments)!;

        Assert.True(accepted);
        var indexed = Assert.IsAssignableFrom<IReadOnlyDictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact>>(arguments[2]);
        Assert.Equal(30, Assert.Single(indexed).Key.ProcessId);
    }

    [Fact]
    public void MissingRuntimeOmitsOnlyThatProcessFromBothDomains()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var runtime = CreateRuntimeFacts().Where(pair => pair.Key.ProcessId == 10).ToDictionary();
        var exclusions = new List<SchedulingFactExclusion>();

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, runtime,
            CreateCpuResidency(), exclusions: exclusions);

        Assert.NotNull(result?.Cpu);
        Assert.NotNull(result?.Gpu);
        Assert.DoesNotContain(result!.Cpu!.Scores.Concat(result.Gpu!.Scores), row => row.ProcessId == 20);
        Assert.Equal(1U, Assert.Single(result.Cpu.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).MemberCount);
        Assert.Contains(exclusions, exclusion => exclusion.ProcessId == 20
            && exclusion.Fact == SchedulingFactCode.RuntimeState);
        Assert.Equal(1U, result.Welfare.EligibleProcessCount);
    }

    [Fact]
    public void PartialInventoryKeepsPresentProcessScoresWithoutProvingAbsence()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10) with { SkippedCount = uint.MaxValue };
        Assert.False(facts.IsInventoryCurrentComplete());

        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency());

        Assert.NotNull(result?.Cpu);
        Assert.NotNull(result?.Gpu);
        Assert.Equal(2, result!.Cpu!.Scores.Count(row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu));
    }

    [Fact]
    public void AbsentCoveredProcessScoresZeroAndKeepsSoftwareMembership()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 25)));
        var exclusions = new List<SchedulingFactExclusion>();

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(),
            physical, exclusions: exclusions);

        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, result?.CpuOccupancySource);
        Assert.Equal(0, Assert.Single(result!.Cpu!.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 20).Score);
        Assert.Contains(result.Gpu!.Scores, row => row.ProcessId == 20);
        Assert.Equal(2U, Assert.Single(result.Cpu.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).MemberCount);
        Assert.DoesNotContain(exclusions, exclusion => exclusion.ProcessId == 20
            && exclusion.Fact == SchedulingFactCode.CpuCoreResidency);
    }

    [Fact]
    public void AmbiguousCoreIdentityDoesNotEraseOtherProcessCpuOrGpu()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 25)),
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 50)),
            CpuCoreResidencyTestValues.Process(20, SecondStartKey, ("core:0", 0)));

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), physical);

        Assert.Equal(20, Assert.Single(result!.Cpu!.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu).ProcessId);
        Assert.Contains(result.Gpu!.Scores, row => row.ProcessId == 10);
    }

    [Fact]
    public void InvalidScalarCpuMetricKeepsOtherCpuAndItsOwnGpu()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        facts = facts with { Processes = facts.Processes.Select(process => process.ProcessId == 10
            ? process with { CpuUsagePercent = double.NaN } : process).ToArray() };

        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), null);

        Assert.Equal(20, Assert.Single(result!.Cpu!.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu).ProcessId);
        Assert.Contains(result.Gpu!.Scores, row => row.ProcessId == 10);
    }
}
