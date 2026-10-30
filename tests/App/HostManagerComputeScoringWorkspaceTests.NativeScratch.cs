using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    [Fact]
    public void SmallEnvelopeUsesCompactScratchWithoutRegisteringALedgerSlot()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => resource = owner.CreateScratch(length));
        var inventory = CreateInventory();
        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), CreateCpuResidency());
        Assert.NotNull(result?.Cpu);
        Assert.True(resource!.Length < owner.NativePageSize);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
    }

    [Fact]
    public void SparseCoreRowsUseTheSameBytesWithLargerConfiguredCoreAndProcessLimits()
    {
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        ulong Measure(uint cores, bool largerLimits)
        {
            var configuration = CreateConfiguration(cpuCoreCount: cores);
            if (largerLimits)
            {
                configuration.MaximumProcessCount = 65_536;
                configuration.MaximumGpuRowCount = 65_536;
                configuration.MaximumOutputCount = 327_680;
            }
            PoisonedScratch? resource = null;
            using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
                HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(
                    Enumerable.Repeat(1, checked((int)cores)).ToArray())),
                length => resource = new PoisonedScratch(length));
            Assert.NotNull(workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency())?.Cpu);
            return resource!.Length;
        }
        Assert.Equal(Measure(1, false), Measure(64, true));
    }

    [Fact]
    public void EachRoundOpensOneRealRegionSizedForItsOwnRowsAfterThePreviousIsClosed()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var configuration = CreateConfiguration();
        using var expectedSession = new NativeComputeScoringSession(in configuration, [1]);
        var capacity = expectedSession.Capacity;
        var inventory = CreateInventory();
        var full = CreateLedgerSizedFacts(inventory);
        var small = full with { Processes = full.Processes.Take(5).ToArray(), EnumeratedCount = 5, EmittedCount = 5 };
        ulong Expected(ResourceManager.App.Domain.ResourceBreakdown.SchedulingProcessFactSnapshot facts)
        {
            var processes = checked((uint)facts.Processes.Count);
            var gpuRows = checked((uint)facts.Processes.Sum(static process => process.Gpus.Count));
            return new ComputeScoringScratchLayout(processes, gpuRows, Math.Max(processes * 3, gpuRows * 2), 0,
                NativeComputeScoringSession.ScratchByteCount(processes, gpuRows), capacity.ScratchAlignment,
                inventory.Adapters.Count).Length;
        }
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), length =>
            {
                // A round opens its region only after the previous round's region is closed.
                Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
                Assert.All(resources, static previous => Assert.Throws<ObjectDisposedException>(() => previous.Acquire()));
                var next = owner.CreateScratch(length);
                resources.Add(next);
                return next;
            });
        Assert.Empty(resources);
        var first = workspace.Score(1, full, inventory, LedgerRuntimeFacts(full), null)!;
        var reduced = workspace.Score(2, small, inventory, LedgerRuntimeFacts(small), null)!;
        var again = workspace.Score(3, full, inventory, LedgerRuntimeFacts(full), null)!;
        // One region per round holds both domains and the native work, sized for that round's rows.
        Assert.Equal(new[] { Expected(full), Expected(small), Expected(full) }, resources.Select(static resource => resource.Length));
        Assert.True(resources[0].Length >= owner.NativePageSize);
        Assert.True(resources[1].Length < resources[0].Length);
        Assert.All(resources, static resource => Assert.Throws<ObjectDisposedException>(() => resource.Acquire()));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.NotNull(reduced.Cpu);
        Assert.NotNull(reduced.Gpu);
        Assert.Equal(first.Cpu!.Scores.Select(value => value.Score), again.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(first.Gpu!.Scores.Select(value => value.Score), again.Gpu!.Scores.Select(value => value.Score));
        var repeated = workspace.Score(3, full, inventory, LedgerRuntimeFacts(full), null)!;
        Assert.Equal(3UL, repeated.RoundNumber);
        Assert.Equal<HostManagerComputeScore>(again.Cpu.Scores, repeated.Cpu!.Scores);
        Assert.Equal<HostManagerComputeScore>(again.Gpu.Scores, repeated.Gpu!.Scores);
        Assert.Equal(4, resources.Count);
    }
}
