using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;
using Microsoft.Extensions.Logging;

namespace Resource_Manager_APP.Tests;

public sealed class RowSizedComputeScratchTests
{
    [Fact]
    public void GrowthLogsOnceAndDiagnosticsExposeTheBoundRows()
    {
        var logger = new WorkspaceLogger();
        using var owner = new ResourceManagerLedgerOwner(logger);
        using var scratch = new RowSizedComputeScratch(owner.CreateScratch, "visible-workspace");
        scratch.Ensure(8192, "rows=10");
        scratch.Ensure(8192, "rows=10");
        scratch.Ensure(4096, "rows=5");
        scratch.Ensure(16384, "rows=20");
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("rows=10 -> rows=20", logger.Messages[1]);
        Assert.Contains("actual row demand", logger.Messages[1]);
        var capacity = Assert.Single(owner.ReadWorkspaceCapacities());
        Assert.Equal("visible-workspace", capacity.WorkArea);
        Assert.Equal("rows=20", capacity.Rows);
        Assert.Equal(16384UL, capacity.Bytes);
        Assert.True(capacity.InLedger);
    }

    private sealed class WorkspaceLogger : ILogger<ResourceManagerLedgerOwner>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }

    [Fact]
    public void FailedFactoryLeavesNoBindingAndSmallerRequestExplicitlyRecreatesIt()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var fail = false;
        using var scratch = new RowSizedComputeScratch(bytes => fail
            ? throw new IOException("recorded factory failure") : owner.CreateScratch(bytes), "retry-rows");
        scratch.Ensure(8192, "rows=1");
        fail = true;
        Assert.Throws<IOException>(() => scratch.Ensure(16384, "rows=2"));
        Assert.Equal(0UL, scratch.Length);
        Assert.Throws<InvalidOperationException>(() => scratch.Acquire());
        Assert.Empty(owner.ReadWorkspaceCapacities());
        fail = false;
        scratch.Ensure(8192, "rows=1");
        using var use = scratch.Acquire();
        use.Complete();
        Assert.Equal(8192UL, scratch.Length);
        Assert.Single(owner.ReadWorkspaceCapacities());
    }

    [Fact]
    public void SmallRowsUseCompactStorageAndGrowthClosesOldBeforeCreatingOneRegistration()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var scratch = new RowSizedComputeScratch(bytes =>
        {
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            if (regions.Count != 0) Assert.True(regions[^1].Closed);
            var region = owner.CreateScratch(bytes);
            regions.Add(region);
            return region;
        }, "rows-under-test");
        scratch.Ensure(320, "rows=1");
        using (var use = scratch.Acquire()) use.Complete();
        Assert.False(regions[0].InLedger);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        scratch.Ensure(8192, "rows=26");
        using (var use = scratch.Acquire()) use.Complete();
        Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes);
        Assert.True(regions[0].Closed);
        Assert.Equal("rows=26", Assert.Single(owner.ReadWorkspaceCapacities()).Rows);
        scratch.Ensure(320, "rows=1");
        scratch.Ensure(8192, "rows=26");
        Assert.Equal(2, regions.Count);
        var uid = regions[1].Uid;
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        using (var use = scratch.Acquire()) use.Complete();
        Assert.Equal(uid, regions[1].Uid);
        Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes);
        scratch.Ensure(16384, "rows=52");
        Assert.True(regions[1].Closed);
        Assert.NotEqual(uid, regions[2].Uid);
        using (var use = scratch.Acquire()) use.Complete();
        Assert.Equal(16384UL, owner.ReadTable().RegisteredBytes);
        Assert.Single(owner.ReadWorkspaceCapacities());
    }

    [Fact]
    public void FailedGrowthCloseRetainsOldObligationAndNeverCreatesAnotherSlot()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var scratch = new RowSizedComputeScratch(bytes =>
        {
            var region = owner.CreateScratch(bytes);
            regions.Add(region);
            return region;
        }, "held-rows");
        scratch.Ensure(8192, "rows=1");
        using (var use = scratch.Acquire())
        {
            use.Complete();
            Assert.Throws<LedgerException>(() => scratch.Ensure(16384, "rows=2"));
            Assert.Single(regions);
            Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes);
        }
        // Even smaller demand must retry the old close, not reuse its retired handle.
        scratch.Ensure(8192, "rows=1");
        Assert.True(regions[0].Closed);
        Assert.Equal(2, regions.Count);
        using var refilled = scratch.Acquire();
        refilled.Complete();
    }
}
