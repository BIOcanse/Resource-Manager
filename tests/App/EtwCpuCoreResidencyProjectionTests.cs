using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuCoreResidencyProjectionTests
{
    [Fact]
    public void DurationDeterminesProcessAndCoreOrderWhenSwitchCountsAreEqual()
    {
        var plan = CompilePlan(1, 1);
        var aggregate = new CpuResidencyAggregationSnapshot(
            1000,
            0,
            1000,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            true,
            [
                new CpuExecutionTimeAggregate(1, 100, 1000, "Long", 1, 10, 0, 900, 1),
                new CpuExecutionTimeAggregate(1, 100, 1000, "Long", 1, 10, 1, 100, 1),
                new CpuExecutionTimeAggregate(2, 200, 2000, "Short", 2, 20, 1, 200, 1)
            ]);

        var processes = EtwCpuCoreResidencyReader.BuildProcessSnapshot(plan, 7, aggregate);

        Assert.Equal([100, 200], processes.Select(static process => process.ProcessId));
        var longProcess = processes[0];
        Assert.Equal(1000, longProcess.ExecutionTimeMilliseconds);
        Assert.Equal(0, longProcess.PrimaryLogicalProcessorId);
        Assert.Equal("core:0", longProcess.PrimaryPhysicalCoreId);
        Assert.Equal(["7:1", "7:2"], processes.Select(static process => process.ProcessInstanceId));
        Assert.Equal(2, longProcess.SwitchCount);
        Assert.Equal(900, longProcess.LogicalProcessors[0].ExecutionTimeMilliseconds);
        Assert.Equal(90, longProcess.LogicalProcessors[0].SharePercent);
        Assert.Equal(90, longProcess.PhysicalCores[0].UsagePercent);
    }

    [Theory]
    [InlineData(1, 1000, 0, 100)]
    [InlineData(2, 1000, 0, 50)]
    [InlineData(2, 0, 1000, 50)]
    [InlineData(2, 1000, 1000, 100)]
    [InlineData(2, 500, 0, 25)]
    [InlineData(2, 500, 500, 50)]
    [InlineData(4, 1000, 0, 25)]
    public void PhysicalUsageDividesByAllSiblingsNotOnlyActiveThreads(
        int siblings, long firstDuration, long secondDuration, double expectedUsage)
    {
        var records = new List<CpuExecutionTimeAggregate>
        {
            new(1, 100, 1000, "Process", 1, 10, 0, firstDuration, 1)
        };
        if (siblings > 1)
            records.Add(new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 2, 11, 1, secondDuration, 1));
        var aggregate = new CpuResidencyAggregationSnapshot(1000, 5000, 6000,
            DateTimeOffset.UnixEpoch.AddSeconds(6), true, records);
        var process = Assert.Single(EtwCpuCoreResidencyReader.BuildProcessSnapshot(
            CompilePlan(siblings), 7, aggregate));

        var core = Assert.Single(process.PhysicalCores);
        Assert.Equal(expectedUsage, core.UsagePercent, 10);
        Assert.Equal(100, core.SharePercent);
    }

    [Fact]
    public void PhysicalUsageAddsThreadsAndMigrationsWithoutDuplicatingCapacity()
    {
        var aggregate = new CpuResidencyAggregationSnapshot(1000, 0, 1000,
            DateTimeOffset.UnixEpoch.AddSeconds(1), true,
            [
                new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 1, 10, 0, 400, 3),
                new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 1, 10, 1, 200, 9),
                new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 2, 11, 0, 600, 1),
                new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 2, 11, 2, 200, 2)
            ]);
        var process = Assert.Single(EtwCpuCoreResidencyReader.BuildProcessSnapshot(
            CompilePlan(2, 1), 7, aggregate));

        Assert.Equal(60, process.PhysicalCores.Single(core => core.PhysicalCoreId == "core:0").UsagePercent);
        Assert.Equal(20, process.PhysicalCores.Single(core => core.PhysicalCoreId == "core:1").UsagePercent);
        Assert.Equal(1400, process.ExecutionTimeMilliseconds);
        Assert.Equal(2, process.ThreadCount);
    }

    [Fact]
    public void TwoProcessesOnSiblingsShareOnePhysicalCoreCapacity()
    {
        var aggregate = new CpuResidencyAggregationSnapshot(1000, 0, 1000,
            DateTimeOffset.UnixEpoch.AddSeconds(1), true,
            [
                new CpuExecutionTimeAggregate(1, 100, 1000, "First", 1, 10, 0, 1000, 1),
                new CpuExecutionTimeAggregate(2, 200, 2000, "Second", 2, 20, 1, 1000, 1)
            ]);
        var processes = EtwCpuCoreResidencyReader.BuildProcessSnapshot(CompilePlan(2), 7, aggregate);

        Assert.Equal(2, processes.Count);
        Assert.All(processes, process => Assert.Equal(50, Assert.Single(process.PhysicalCores).UsagePercent));
        Assert.Equal(100, processes.Sum(process => process.PhysicalCores[0].UsagePercent));
    }

    [Fact]
    public void PhysicalUsageUsesRawWindowTicksInsteadOfRoundedDisplayMilliseconds()
    {
        var aggregate = new CpuResidencyAggregationSnapshot(10000000, 100, 103,
            DateTimeOffset.UnixEpoch, true,
            [new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 1, 10, 0, 1, 1)]);
        var process = Assert.Single(EtwCpuCoreResidencyReader.BuildProcessSnapshot(
            CompilePlan(2), 7, aggregate));
        var core = Assert.Single(process.PhysicalCores);

        Assert.Equal(0, core.ExecutionTimeMilliseconds);
        Assert.Equal(100.0 / 6, core.UsagePercent, 10);
    }

    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 0)]
    public void IncompleteOrEmptyWindowsCannotProducePhysicalUsage(bool complete, long endQpc)
    {
        var aggregate = new CpuResidencyAggregationSnapshot(1000, 0, endQpc,
            DateTimeOffset.UnixEpoch, complete, []);
        Assert.Throws<InvalidDataException>(() =>
            EtwCpuCoreResidencyReader.BuildProcessSnapshot(CompilePlan(2), 7, aggregate));
    }

    private static CompiledCpuCoreResidencyPlan CompilePlan(params int[] siblingsPerCore)
        => HostManagerTestPlanFactory.CreatePlan(
            cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(siblingsPerCore)).CpuCoreResidency;
}
