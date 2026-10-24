using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class PublicServiceLedgerScratchTests
{
    [Fact]
    public void FailedEndUseRetainsTheRealLeaseUntilTheNextOperationRetries()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan().PublicServiceCoordinator;
        using var workspace = new NativePublicServiceCoordinatorWorkspace(plan, 1, 1, 1,
            length => new FailingScratch(ledger.CreateScratch(length), failReturn: true), new());
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Throws<IOException>(() => workspace.ResolveModel("unavailable"));
        Assert.Equal(34_291_719UL, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(0UL, ledger.Tick().Released);
        var output = workspace.ResolveModel("unavailable");
        Assert.Equal((uint)NativePublicServiceModelResolveStatus.CatalogUnavailable, output.Status);
        Assert.Equal(1UL, ledger.Tick().Released);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedResourceCloseCanBeRetriedWithoutDestroyingTheSession()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan().PublicServiceCoordinator;
        using var workspace = new NativePublicServiceCoordinatorWorkspace(plan, 1, 1, 1,
            length => new FailingScratch(ledger.CreateScratch(length), failClose: true), new());
        workspace.ResolveModel("unavailable");
        Assert.Throws<IOException>(() => workspace.Dispose());
        Assert.Equal(34_291_719UL, ledger.ReadTable().RegisteredBytes);
        Assert.NotEqual(0UL, workspace.ReadSnapshot().CatalogGeneration);
        workspace.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    private sealed class FailingScratch(IComputeScratch inner, bool failReturn = false, bool failClose = false)
        : IComputeScratch
    {
        private bool returnFailure = failReturn, closeFailure = failClose;
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => new Reading(this, inner.Acquire());
        public void Dispose()
        {
            if (closeFailure) { closeFailure = false; throw new IOException("Injected close failure."); }
            inner.Dispose();
        }

        private sealed class Reading(FailingScratch owner, ComputeScratchLease inner) : ComputeScratchLease
        {
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete() => inner.Complete();
            public override void Dispose()
            {
                if (owner.returnFailure) { owner.returnFailure = false; throw new IOException("Injected endUse failure."); }
                inner.Dispose();
            }
        }
    }
}
