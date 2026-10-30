using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaDrsHostZoneAndPathTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(1, 1, 1, 1, 20_000);

    [Fact]
    public void ZoneRetainsFailedCleanupForExplicitRetry()
    {
        var api = new NvidiaDrsHostRecording();
        var reader = new NvidiaNvapiReader(api);
        var zone = new NvidiaNvapiMonitoringZone(reader);
        Assert.NotNull(zone.ReadDrs([new("Game", new uint[] { 7 })], Limits, default));
        api.FailDestroy = true;
        Assert.Null(zone.ReadDrs([new("Game", new uint[] { 7 })], Limits, default));
        Assert.Throws<InvalidOperationException>(zone.Dispose);
        Assert.Equal(0, api.Unloads); Assert.Equal(0, api.Frees);
        api.FailDestroy = false; api.FailUnload = true;
        Assert.Throws<InvalidOperationException>(zone.Dispose);
        Assert.Equal(1, api.Unloads); Assert.Equal(0, api.Frees);
        api.FailUnload = false; zone.Dispose();
        Assert.Equal(2, api.Unloads); Assert.Equal(1, api.Frees);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadDrs([], new(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void SelectorHelperPreservesMaximumNameAndRejectsOversizedCompleteSelection()
    {
        var name = new string('界', 2047);
        var token = NvidiaDrsSubscriptionPath.EncodeProfileName(name);
        Assert.Equal(name, NvidiaDrsSubscriptionPath.DecodeProfileName(token));
        var path = NvidiaDrsSubscriptionPath.Create(new(name, new uint[] { 0 }), Limits, TimeSpan.FromMilliseconds(100));
        Assert.True(path.Length < 8192);
        Assert.ThrowsAny<ArgumentException>(() => NvidiaDrsSubscriptionPath.EncodeProfileName("\uD800"));
        Assert.ThrowsAny<ArgumentException>(() => NvidiaDrsSubscriptionPath.DecodeProfileName("ANg"));
        var many = new NvidiaDrsReadLimits(1, 1000, 1000, 1, 20_000_000);
        Assert.Throws<ArgumentException>(() => NvidiaDrsSubscriptionPath.Create(
            new(name, Enumerable.Range(0, 1000).Select(index => (uint)index).ToArray()), many, TimeSpan.FromMilliseconds(100)));
    }
}
