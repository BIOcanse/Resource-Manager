using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class PdhParsingScratchTests
{
    [Fact]
    public void SampleUsesOneRealLeaseAndIdleReclaimRefillsTheSameIdentity()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var factories = 0;
        using var scratch = new PdhParsingScratch(length => { factories++; return resource = ledger.CreateScratch(length); }, ledger.NativePageSize);
        scratch.BeginSample();
        var first = scratch.Supply(8192);
        var uid = resource!.Uid;
        Assert.Equal(0UL, (ulong)first.Address.ToInt64() % 8);
        Assert.True(first.Length >= 8192);
        Assert.Equal(1, resource.Active);
        Marshal.WriteInt64(first.Address, 0x12345678);
        Assert.Equal(first.Address, scratch.Supply(4096).Address);
        Assert.Equal(1, resource.Active);
        Assert.Equal(0UL, ledger.Tick().Released);
        scratch.EndSample();
        Assert.Equal(0, resource.Active);
        scratch.BeginSample();
        Assert.Equal(0x12345678, Marshal.ReadInt64(scratch.Supply(8192).Address));
        scratch.EndSample();
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        scratch.BeginSample();
        scratch.Supply(8192);
        Assert.Equal(uid, resource.Uid);
        Assert.Equal(1, factories);
        Assert.Equal(resource.Length, ledger.ReadTable().RegisteredBytes);
        scratch.EndSample();
    }

    [Fact]
    public void TinyRequestsStayOrdinaryAndGrowthClosesTheOldIdentityBeforeAllocating()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var scratch = new PdhParsingScratch(length => { var item = ledger.CreateScratch(length); resources.Add(item); return item; }, ledger.NativePageSize);
        scratch.BeginSample();
        scratch.Supply(17);
        Assert.False(resources[0].InLedger);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        scratch.Supply(8192);
        Assert.True(resources[0].Closed);
        Assert.Equal(0, resources[0].Active);
        Assert.True(resources[1].InLedger);
        Assert.NotEqual(resources[0].Uid, resources[1].Uid);
        Assert.Equal(0UL, resources[1].Length % ledger.NativePageSize);
        Assert.Equal(resources[1].Length, ledger.ReadTable().RegisteredBytes);
        scratch.EndSample();
        scratch.Dispose();
        Assert.True(resources[1].Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnFailureRetainsTheRealLeaseUntilExplicitRetry(bool failComplete)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resource = ledger.CreateScratch(12288);
        var recorded = new RecordedResource(resource);
        using var scratch = new PdhParsingScratch(_ => recorded, ledger.NativePageSize);
        scratch.BeginSample(); scratch.Supply(8192);
        recorded.FailComplete = failComplete;
        recorded.FailEnd = !failComplete;
        Assert.Throws<IOException>(() => scratch.EndSample());
        Assert.Equal(1, resource.Active);
        Assert.Equal(0, recorded.Ends);
        Assert.Equal(0UL, ledger.Tick().Released);
        Assert.Throws<InvalidOperationException>(() => scratch.BeginSample());
        scratch.RetryReturn();
        Assert.Equal(0, resource.Active);
        Assert.Equal(1, recorded.Ends);
        scratch.RetryReturn();
        Assert.Equal(1, recorded.Ends);
        scratch.BeginSample(); scratch.Supply(8192); scratch.EndSample();
        Assert.Equal(2, recorded.Ends);
    }

    [Fact]
    public void FailedGrowthCloseDoesNotAllocateFallbackOrRetryInsideTheSample()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var old = ledger.CreateScratch(8192);
        var recorded = new RecordedResource(old) { FailClose = true };
        var factories = 0;
        using var scratch = new PdhParsingScratch(length => ++factories == 1 ? recorded : ledger.CreateScratch(length), ledger.NativePageSize);
        scratch.BeginSample();
        var first = scratch.Supply(4096);
        var growth = checked(first.Length + 1);
        try { Assert.Throws<IOException>(() => scratch.Supply(growth)); }
        finally { scratch.EndSample(); }
        Assert.Equal(1, factories);
        Assert.Equal(1, recorded.CloseAttempts);
        Assert.False(old.Closed);
        Assert.Equal(0, old.Active);
        Assert.Throws<InvalidOperationException>(() => scratch.BeginSample());
        scratch.RetryReturn();
        Assert.True(old.Closed);
        Assert.Equal(2, recorded.CloseAttempts);
        scratch.BeginSample(); scratch.Supply(growth); scratch.EndSample();
        Assert.Equal(2, factories);
    }

    internal sealed class RecordedResource(ResourceManagerLedgerOwner.ComputeScratch resource) : IComputeScratch
    {
        internal bool FailEnd, FailComplete, FailClose;
        internal int Ends, CloseAttempts;
        public ulong Length => resource.Length;
        public ComputeScratchLease Acquire() => new Lease(this, resource.Acquire());
        public void Dispose()
        {
            CloseAttempts++;
            if (FailClose) { FailClose = false; throw new IOException("recorded PDH close failure"); }
            resource.Dispose();
        }
        private sealed class Lease(RecordedResource owner, ComputeScratchLease use) : ComputeScratchLease
        {
            private bool ended;
            internal override ulong Length => use.Length;
            internal override ulong AllocationNumber => use.AllocationNumber;
            internal override bool IsLoading => use.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => use.GetMemoryChunk(offset, count);
            internal override void Complete()
            {
                if (owner.FailComplete) { owner.FailComplete = false; throw new IOException("recorded PDH completion failure"); }
                use.Complete();
            }
            public override void Dispose()
            {
                if (ended) return;
                if (owner.FailEnd) { owner.FailEnd = false; throw new IOException("recorded PDH endUse failure"); }
                use.Dispose(); ended = true; owner.Ends++;
            }
        }
    }
}
