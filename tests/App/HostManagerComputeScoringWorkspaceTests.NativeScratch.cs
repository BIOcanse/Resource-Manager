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
    public void GrowthRetiresOldUidAndReclaimRefillsOnlyTheNewUid()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), length =>
            {
                var next = owner.CreateScratch(length);
                resources.Add(next);
                return next;
            });
        var inventory = CreateInventory();
        var full = CreateLedgerSizedFacts(inventory);
        var small = full with { Processes = full.Processes.Take(5).ToArray(), EnumeratedCount = 5, EmittedCount = 5 };
        workspace.Score(1, small, inventory, LedgerRuntimeFacts(small), null);
        var previous = Assert.Single(resources);
        var result = workspace.Score(2, full, inventory, LedgerRuntimeFacts(full), null)!;
        Assert.Equal(2, resources.Count);
        var current = resources[1];
        Assert.NotEqual(previous.Uid, current.Uid);
        Assert.Throws<ObjectDisposedException>(() => previous.Acquire());
        Assert.Equal(current.Length, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var refill = workspace.Score(3, full, inventory, LedgerRuntimeFacts(full), null)!;
        Assert.Equal(2, resources.Count);
        Assert.Equal(current.Length, owner.ReadTable().RegisteredBytes);
        Assert.Equal(result.Cpu!.Scores.Select(value => value.Score), refill.Cpu!.Scores.Select(value => value.Score));
    }

    [Fact]
    public void BothDomainsUseOneRealRegionIncludingNativeWorkAndRefillAfterReclaim()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var creates = 0;
        var configuration = CreateConfiguration();
        using var expectedSession = new NativeComputeScoringSession(in configuration, [1]);
        var capacity = expectedSession.Capacity;
        var inventory = CreateInventory();
        var facts = CreateLedgerSizedFacts(inventory);
        var runtime = LedgerRuntimeFacts(facts);
        var gpuRows = checked((uint)facts.Processes.Sum(static process => process.Gpus.Count));
        var nativeBytes = NativeComputeScoringSession.ScratchByteCount(checked((uint)facts.Processes.Count), gpuRows);
        var coreRows = 0;
        var expectedLayout = new ComputeScoringScratchLayout(checked((uint)facts.Processes.Count), gpuRows,
            Math.Max(checked((uint)facts.Processes.Count * 3), gpuRows * 2), coreRows, nativeBytes,
            capacity.ScratchAlignment, inventory.Adapters.Count);
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => { creates++; return resource = owner.CreateScratch(length); });
        Assert.Equal(0, creates);
        Assert.Null(resource);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var first = workspace.Score(1, facts, inventory, runtime, null)!;
        Assert.Equal(1, creates);
        Assert.Equal(expectedLayout.Length, resource!.Length);
        var savedCpu = first.Cpu!.Scores.ToArray();
        var savedGpu = first.Gpu!.Scores.ToArray();
        var uid = resource.Uid;
        Assert.Equal(expectedLayout.Length, owner.ReadTable().RegisteredBytes); // Not two Native regions/UIDs.
        ulong allocation;
        using (var lease = resource.Acquire())
        {
            allocation = lease.AllocationNumber;
            using (var view = new ComputeScratchView(lease))
            {
                // Poison every byte of the shared Native extent, then rely on actual score refill.
                var pointer = view.AlignedRegion(expectedLayout.NativeScratch,
                    nativeBytes, capacity.ScratchAlignment);
                System.Runtime.InteropServices.Marshal.Copy(
                    Enumerable.Repeat((byte)0xA5, checked((int)nativeBytes)).ToArray(),
                    0, pointer, checked((int)nativeBytes));
            }
            lease.Complete();
        }
        var afterPoison = workspace.Score(2, facts, inventory, runtime, null)!;
        Assert.Equal(savedCpu.Select(value => value.Score), afterPoison.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(savedGpu.Select(value => value.Score), afterPoison.Gpu!.Scores.Select(value => value.Score));
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var afterReclaim = workspace.Score(3, facts, inventory, runtime, null)!;
        Assert.Equal(uid, resource.Uid);
        using (var lease = resource.Acquire()) Assert.NotEqual(allocation, lease.AllocationNumber);
        Assert.Equal(expectedLayout.Length, owner.ReadTable().RegisteredBytes);
        Assert.Equal(savedCpu.Select(value => value.Score), afterReclaim.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(savedGpu.Select(value => value.Score), afterReclaim.Gpu!.Scores.Select(value => value.Score));
        Assert.Equal<HostManagerComputeScore>(savedCpu, first.Cpu.Scores);
        Assert.Equal<HostManagerComputeScore>(savedGpu, first.Gpu.Scores);
        var repeated = workspace.Score(3, facts, inventory,
            runtime, null)!;
        Assert.Equal(3UL, repeated.RoundNumber);
        Assert.Equal<HostManagerComputeScore>(afterReclaim.Cpu!.Scores, repeated.Cpu!.Scores);
        Assert.Equal<HostManagerComputeScore>(afterReclaim.Gpu!.Scores, repeated.Gpu!.Scores);
        Assert.Equal(1, creates);
    }
}
