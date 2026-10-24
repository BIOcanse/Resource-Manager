using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    // Explicit algorithm fixture only. Production always receives the original owner's factory.
    private static IComputeScratch CreateScratchFixture(ulong length) => new PoisonedScratch(length);

    private sealed class PoisonedScratch(ulong length) : IComputeScratch
    {
        private readonly byte[] bytes = new byte[checked((int)length)];
        internal int Acquires;
        internal int Returns;
        internal bool Disposed;
        public ulong Length => length;
        public ComputeScratchLease Acquire()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Array.Fill(bytes, (byte)0xA5); // A stale/tail read must not accidentally see zero.
            Acquires++;
            return new Reading(this);
        }
        public void Dispose() => Disposed = true;
        private sealed class Reading(PoisonedScratch owner) : ComputeScratchLease
        {
            private bool disposed;
            internal override ulong Length => owner.Length;
            internal override ulong AllocationNumber => 0;
            internal override bool IsLoading => false;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return owner.bytes.AsMemory(checked((int)offset), count);
            }
            internal override void Complete() => ObjectDisposedException.ThrowIf(disposed, this);
            public override void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.Returns++;
            }
        }
    }

    [Fact]
    public void RealScratchReallocatesAndRecomputesTheSameRoundWithoutChangingReturnedScores()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => resource = owner.CreateScratch(length));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // Constructor is lazy.
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var first = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        var saved = first!.Cpu!.Scores.ToArray();
        Assert.NotNull(resource);
        var uid = resource.Uid;
        ulong allocation;
        using (var lease = resource.Acquire()) allocation = lease.AllocationNumber;
        Assert.NotEqual(0UL, allocation);
        Assert.Equal(resource.Length, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Equal<ResourceManager.App.Infrastructure.Optimization.HostManagerComputeScore>(saved, first.Cpu.Scores);
        var second = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        Assert.Equal(1UL, second!.RoundNumber);
        Assert.Equal(uid, resource.Uid);
        using (var lease = resource.Acquire()) Assert.NotEqual(allocation, lease.AllocationNumber);
        Assert.Equal(first.Cpu.Scores.Select(value => value.Score), second!.Cpu!.Scores.Select(value => value.Score));
    }

    [Fact]
    public void FailedScoreReturnsItsLeaseAndAbortsTheFirstLoadingAllocation()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            owner.CreateScratch);
        var inventory = CreateInventory();
        Assert.Throws<InvalidDataException>(() => workspace.Score(1, CreateFacts(inventory, 10), inventory,
            CreateRuntimeFacts().ToDictionary(pair => pair.Key, pair => pair.Value with { CpuPolicyMultiplier = double.NaN }), CreateCpuResidency()));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.NotNull(workspace.Score(2, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), CreateCpuResidency()));
    }

    [Fact]
    public void PoisonedBorrowIsReturnedOnFailureAndBothDomainsRebuildTheirLivePrefixes()
    {
        PoisonedScratch? resource = null;
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => resource = new PoisonedScratch(length));
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var first = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        var cpuOnly = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency(), scoreGpu: false);
        var gpuOnly = workspace.Score(3, facts, inventory, CreateRuntimeFacts(), null, scoreCpu: false);
        Assert.Equal(first!.Cpu!.Scores.Select(value => value.Score), cpuOnly!.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(first.Gpu!.Scores.Select(value => value.Score), gpuOnly!.Gpu!.Scores.Select(value => value.Score));
        var smallerFacts = facts with
        {
            Processes = facts.Processes.Take(1).ToArray(), EnumeratedCount = 1, EmittedCount = 1,
            SkippedCount = 0
        };
        var smallerRuntime = CreateRuntimeFacts().Where(pair => pair.Key.ProcessId == 10).ToDictionary();
        var smaller = workspace.Score(4, smallerFacts, inventory, smallerRuntime, CreateCpuResidency());
        Assert.NotNull(smaller?.Cpu);
        Assert.DoesNotContain(smaller!.Cpu!.Scores, value => value.ProcessId == 20);
        Assert.DoesNotContain(smaller.Gpu!.Scores, value => value.ProcessId == 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Score(0, facts, inventory,
            CreateRuntimeFacts(), CreateCpuResidency()));
        Assert.Equal(4, resource!.Acquires);
        Assert.Equal(resource.Acquires, resource.Returns);
    }

    [Fact]
    public void IncompleteInventoryReturnsBeforeAnyScratchAllocation()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            owner.CreateScratch);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10) with
        {
            InventoryStatus = ResourceManager.App.Domain.Metrics.SamplingObservationStatus.RetainedLastGood
        };
        Assert.Null(workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency()));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void WrongFactoryLayoutIsRetiredDuringConstructorFailure()
    {
        PoisonedScratch? wrong = null;
        var configuration = CreateConfiguration();
        Assert.Throws<InvalidOperationException>(() => new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => wrong = new PoisonedScratch(length + 8)));
        Assert.True(wrong!.Disposed);
        Assert.Equal(0, wrong.Acquires);
    }
}
