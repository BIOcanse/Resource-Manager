using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    [Fact]
    public void BothDomainsUseOneRealRegionIncludingNativeWorkAndRefillAfterReclaim()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var creates = 0;
        var configuration = CreateConfiguration();
        using var expectedSession = new NativeComputeScoringSession(in configuration, [1]);
        var capacity = expectedSession.Capacity;
        var expectedLayout = new ComputeScoringScratchLayout(capacity.ProcessCapacity, capacity.GpuCapacity,
            capacity.OutputCapacity, 1, capacity.ScratchByteCount, capacity.ScratchAlignment);
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => { creates++; return resource = owner.CreateScratch(length); });
        Assert.Equal(1, creates);
        Assert.Equal(expectedLayout.Length, resource!.Length);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var first = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency())!;
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
                    capacity.ScratchByteCount, capacity.ScratchAlignment);
                System.Runtime.InteropServices.Marshal.Copy(
                    Enumerable.Repeat((byte)0xA5, checked((int)capacity.ScratchByteCount)).ToArray(),
                    0, pointer, checked((int)capacity.ScratchByteCount));
            }
            lease.Complete();
        }
        var afterPoison = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency())!;
        Assert.Equal(savedCpu.Select(value => value.Score), afterPoison.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(savedGpu.Select(value => value.Score), afterPoison.Gpu!.Scores.Select(value => value.Score));
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var afterReclaim = workspace.Score(3, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency())!;
        Assert.Equal(uid, resource.Uid);
        using (var lease = resource.Acquire()) Assert.NotEqual(allocation, lease.AllocationNumber);
        Assert.Equal(expectedLayout.Length, owner.ReadTable().RegisteredBytes);
        Assert.Equal(savedCpu.Select(value => value.Score), afterReclaim.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(savedGpu.Select(value => value.Score), afterReclaim.Gpu!.Scores.Select(value => value.Score));
        Assert.Equal<HostManagerComputeScore>(savedCpu, first.Cpu.Scores);
        Assert.Equal<HostManagerComputeScore>(savedGpu, first.Gpu.Scores);
        Assert.Throws<InvalidOperationException>(() => workspace.Score(3, facts, inventory,
            CreateRuntimeFacts(), CreateCpuResidency())); // Scratch reclaim did not reset accepted native state.
        Assert.Equal(1, creates);
    }
}
