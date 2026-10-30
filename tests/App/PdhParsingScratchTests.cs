using ResourceManager.App.Infrastructure.Adaptation;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class PdhParsingScratchTests
{
    [Fact]
    public void EachSampleCreatesNamedScratchAndEndSampleClosesIt()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var scratch = new PdhParsingScratch(length => { var item = ledger.CreateScratch(length); resources.Add(item); return item; }, ledger.NativePageSize);
        Assert.Empty(resources);
        scratch.BeginSample();
        var first = scratch.Supply(8192);
        var resource = Assert.Single(resources);
        Assert.Equal(0UL, (ulong)first.Address.ToInt64() % 8);
        Assert.True(first.Length >= 8192);
        Assert.Equal(1, resource.Active);
        Marshal.WriteInt64(first.Address, 0x12345678);
        Assert.Equal(first.Address, scratch.Supply(4096).Address);
        Assert.Single(resources);
        Assert.Equal("pdh-parsing", resource.WorkArea);
        Assert.Equal($"bytes={resource.Length}/live=8192", resource.Rows);
        var row = Assert.Single(ledger.ReadWorkspaceCapacities());
        Assert.Equal(("pdh-parsing", resource.Length), (row.WorkArea, row.Bytes));
        Assert.Equal(resource.Length, ledger.ReadTable().RegisteredBytes);
        scratch.EndSample();
        Assert.True(resource.Closed);
        Assert.Empty(ledger.ReadWorkspaceCapacities());
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes); // Nothing is held between samples.
        scratch.BeginSample();
        scratch.Supply(8192);
        Assert.Equal(2, resources.Count);
        Assert.NotEqual(resource.Uid, resources[1].Uid);
        Assert.Equal(resources[1].Length, ledger.ReadTable().RegisteredBytes);
        scratch.EndSample();
        Assert.True(resources[1].Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
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
        Assert.True(resources[1].Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        scratch.Dispose();
        Assert.Equal(2, resources.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnFailureRetainsTheRealLeaseUntilExplicitRetryClosesIt(bool failComplete)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resource = ledger.CreateScratch(12288);
        var recorded = new RecordedResource(resource);
        var factories = 0;
        using var scratch = new PdhParsingScratch(length => ++factories == 1 ? recorded : ledger.CreateScratch(length), ledger.NativePageSize);
        scratch.BeginSample(); scratch.Supply(8192);
        recorded.FailComplete = failComplete;
        recorded.FailEnd = !failComplete;
        Assert.Throws<IOException>(() => scratch.EndSample());
        Assert.Equal(1, resource.Active);
        Assert.Equal(0, recorded.Ends);
        Assert.Equal(0, recorded.CloseAttempts);
        Assert.Equal(0UL, ledger.Tick().Released);
        Assert.Throws<InvalidOperationException>(() => scratch.BeginSample());
        scratch.RetryReturn();
        Assert.Equal(0, resource.Active);
        Assert.Equal(1, recorded.Ends);
        Assert.Equal(1, recorded.CloseAttempts);
        Assert.True(resource.Closed);
        scratch.RetryReturn();
        Assert.Equal((1, 1), (recorded.Ends, recorded.CloseAttempts));
        scratch.BeginSample(); scratch.Supply(8192); scratch.EndSample();
        Assert.Equal(2, factories);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
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
