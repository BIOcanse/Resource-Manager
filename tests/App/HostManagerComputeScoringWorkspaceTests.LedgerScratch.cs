using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    private static ResourceManager.App.Domain.ResourceBreakdown.SchedulingProcessFactSnapshot CreateLedgerSizedFacts(
        ResourceManager.App.Domain.Metrics.SchedulingGpuInventorySnapshot inventory)
    {
        var source = CreateFacts(inventory, 10);
        return source with
        {
            Processes = Enumerable.Range(0, 8).Select(index => source.Processes[0] with
            {
                ProcessId = 10 + index * 10,
                ProcessStartKey = FirstStartKey + checked((ulong)index * 10_000_000)
            }).ToArray(),
            EnumeratedCount = 8, EmittedCount = 8
        };
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> LedgerRuntimeFacts(
        ResourceManager.App.Domain.ResourceBreakdown.SchedulingProcessFactSnapshot facts)
        => facts.Processes.ToDictionary(static process => new HostManagerComputeProcessIdentity(process.ProcessId, process.ProcessStartKey),
            static _ => new HostManagerComputeRuntimeFact(
                ResourceManager.App.Infrastructure.NativeCore.NativeComputeScoringRuntimeState.BackgroundProcess, 1, 1));

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
    public void RealRoundRegionIsReturnedWhenTheRoundEndsAndTheSameRoundRecomputesTheSameScores()
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
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // Constructor is lazy.
        var inventory = CreateInventory();
        var facts = CreateLedgerSizedFacts(inventory);
        var runtime = LedgerRuntimeFacts(facts);
        var first = workspace.Score(1, facts, inventory, runtime, null);
        var saved = first!.Cpu!.Scores.ToArray();
        var round = Assert.Single(resources);
        Assert.True(round.Length >= owner.NativePageSize); // A registered ledger region while the round ran.
        // The lease ended and the region closed with the round, so the ledger has nothing to reclaim.
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => round.Acquire());
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.Equal<ResourceManager.App.Infrastructure.Optimization.HostManagerComputeScore>(saved, first.Cpu.Scores);
        var second = workspace.Score(1, facts, inventory, runtime, null);
        Assert.Equal(1UL, second!.RoundNumber);
        Assert.Equal(2, resources.Count);
        Assert.Throws<ObjectDisposedException>(() => resources[1].Acquire());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
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
        var resources = new List<PoisonedScratch>();
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), length =>
            {
                var resource = new PoisonedScratch(length);
                resources.Add(resource);
                return resource;
            });
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
        // The rejected round opens nothing; each scored round borrowed once, returned and closed its own scratch.
        Assert.Equal(4, resources.Count);
        Assert.All(resources, static resource =>
        {
            Assert.Equal((1, 1), (resource.Acquires, resource.Returns));
            Assert.True(resource.Disposed);
        });
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
    public void WrongFactoryLayoutIsRetiredDuringFirstScoreFailure()
    {
        PoisonedScratch? wrong = null;
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)),
            length => wrong = new PoisonedScratch(length + 8));
        Assert.Null(wrong);
        var inventory = CreateInventory();
        Assert.Throws<InvalidOperationException>(() => workspace.Score(1, CreateFacts(inventory, 10), inventory,
            CreateRuntimeFacts(), CreateCpuResidency()));
        Assert.True(wrong!.Disposed);
        Assert.Equal(0, wrong.Acquires);
    }

    [Fact]
    public void EachRoundSizesItsOwnScratchAndReturnsItBeforeTheNextRoundOpensOne()
    {
        var resources = new List<PoisonedScratch>();
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), length =>
            {
                Assert.All(resources, static previous => Assert.True(previous.Disposed));
                var resource = new PoisonedScratch(length);
                resources.Add(resource);
                return resource;
            });
        Assert.Empty(resources);
        var inventory = CreateInventory();
        var full = CreateFacts(inventory, 10);
        var small = full with { Processes = [full.Processes[0]], EnumeratedCount = 1, EmittedCount = 1 };
        var runtime = CreateRuntimeFacts().Where(pair => pair.Key.ProcessId == 10).ToDictionary();
        var first = workspace.Score(1, small, inventory, runtime, CreateCpuResidency());
        Assert.NotNull(first?.Cpu);
        var original = Assert.Single(resources);
        Assert.True(original.Disposed);
        workspace.Score(2, full, inventory, CreateRuntimeFacts(), CreateCpuResidency());
        Assert.Equal(2, resources.Count);
        Assert.True(resources[1].Length > original.Length);
        // A smaller round after a larger one sizes for its own rows; no earlier round's size is kept.
        workspace.Score(3, small, inventory, runtime, CreateCpuResidency());
        Assert.Equal(3, resources.Count);
        Assert.Equal(original.Length, resources[2].Length);
        Assert.All(resources, static resource =>
        {
            Assert.Equal((1, 1), (resource.Acquires, resource.Returns));
            Assert.True(resource.Disposed);
        });
    }
}
