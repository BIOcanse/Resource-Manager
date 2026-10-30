using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlActualFanActivityTests
{
    [Fact]
    public void OnlySubscriptionEdgesPublishFanActivityAndAuthorityReadsDoNotQueryTheCatalog()
    {
        var catalog = new DeclaredCatalog { HasFan = true };
        using var reader = new ControlActualStateReader(catalog, []);
        var edges = new List<bool>();
        reader.FanReadingsChanged += () => edges.Add(reader.FanReadingsInUse);
        Assert.False(reader.FanReadingsInUse);
        var first = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        var second = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        Assert.Equal([true], edges);
        var reads = catalog.ClassificationReads;
        for (var index = 0; index < 100; index++) Assert.True(reader.FanReadingsInUse);
        Assert.Equal(reads, catalog.ClassificationReads);
        Assert.Equal(0, catalog.Probes);
        first.Dispose();
        Assert.True(reader.FanReadingsInUse);
        Assert.Equal([true], edges);
        second.Dispose();
        second.Dispose();
        Assert.False(reader.FanReadingsInUse);
        Assert.Equal([true, false], edges);
    }

    [Fact]
    public void UnrelatedActualSubscriptionsDoNotEnableFanAndFinalDisposalWithdrawsOutstandingFanReaders()
    {
        var catalog = new DeclaredCatalog();
        using var reader = new ControlActualStateReader(catalog, []);
        var edges = new List<bool>();
        reader.FanReadingsChanged += () => edges.Add(reader.FanReadingsInUse);
        var unrelated = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        Assert.True(reader.ActualControlValuesInUse);
        Assert.False(reader.FanReadingsInUse);
        Assert.Empty(edges);
        catalog.HasFan = true;
        var fan = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        Assert.True(reader.FanReadingsInUse);
        Assert.Equal([true], edges);
        reader.Dispose();
        Assert.False(reader.FanReadingsInUse);
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal([true, false], edges);
        unrelated.Dispose();
        fan.Dispose();
        Assert.Equal([true, false], edges);
        Assert.Throws<ObjectDisposedException>(() => reader.AcquireSubscription(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, catalog.Probes);
    }

    private sealed class DeclaredCatalog : IControlObjectCatalog
    {
        internal bool HasFan;
        internal int ClassificationReads;
        internal int Probes;
        public bool ActualReadingsUseFanCore
        {
            get { ClassificationReads++; return HasFan; }
        }
        public ControlObjectCatalog ReadObjects()
        {
            Probes++;
            throw new InvalidOperationException("Admission must classify declarations without probing the catalog.");
        }
    }
}