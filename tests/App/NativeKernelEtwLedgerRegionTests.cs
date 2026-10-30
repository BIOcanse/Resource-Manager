using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwLedgerRegionTests
{
    [Fact]
    public void CompletedRegionKeepsItsAddressAndBytesAcrossLedgerTicksUntilRelease()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var provider = new NativeKernelEtwLedgerRegionProvider(ledger);
        var region = provider.Allocate("kernel-etw", 16384);
        try
        {
            var address = region.Pointer;
            Marshal.WriteInt64(address, 128, 0x123456789abcdef);
            region.Complete();
            region.Complete();
            Assert.Equal(address, region.Pointer);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            Assert.Equal(0x123456789abcdef, Marshal.ReadInt64(region.Pointer, 128));
            Assert.Equal(16384UL, ledger.ReadTable().RegisteredBytes);
        }
        finally { region.Dispose(); }
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        region.Dispose();
    }

    [Fact]
    public void FailedInitializationDiscardsTheLoadingRegion()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var provider = new NativeKernelEtwLedgerRegionProvider(ledger);
        using (var region = provider.Allocate("kernel-etw", 16384))
            Assert.NotEqual(0, region.Pointer);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Allocate("kernel-etw", 0));
    }

    [Fact]
    public void RetainedAccessIsPublishedRatherThanAnUnfinishedLoadingLease()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var resource = ledger.CreateScratch(16384);
        using var retained = resource.Acquire();
        Assert.True(retained.IsLoading);
        var allocation = retained.AllocationNumber;
        retained.PublishAndRetain();
        Assert.False(retained.IsLoading);
        using var reader = resource.Acquire();
        Assert.False(reader.IsLoading);
        Assert.Equal(allocation, reader.AllocationNumber);
        retained.PublishAndRetain();
    }
}
