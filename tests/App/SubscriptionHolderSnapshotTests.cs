using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class SubscriptionHolderSnapshotTests
{
    [Fact]
    public void TwoHoldersAtDifferentIntervalsAreBothListedAndReleasingOneLeavesTheOther()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var at = DateTimeOffset.UtcNow;
        tracker.TrackPersistent("tray", MetricSampleRequest.ForIds(["cpu.usage"]), at, TimeSpan.FromSeconds(1));
        tracker.TrackPersistent("panel", MetricSampleRequest.ForIds(["cpu.usage", "memory.usage"]), at,
            TimeSpan.FromMilliseconds(250));

        var role = Assert.Single(fixture.Owner.Activity.ReadHolders());
        Assert.Equal(1, role.RoleId);
        Assert.Equal(nameof(MetricSampleRequest), role.RequestType);
        Assert.Equal(fixture.Owner.GetRoleSettings(1)!.DefaultIntervalMilliseconds, role.DefaultIntervalMilliseconds);
        Assert.Equal(new[] { "panel", "tray" }, role.Holders.Select(static holder => holder.Holder));
        var panel = role.Holders[0];
        var tray = role.Holders[1];
        Assert.Equal(250, panel.IntervalMilliseconds);
        Assert.Equal(1000, tray.IntervalMilliseconds);
        Assert.All(role.Holders, holder => Assert.True(holder.ExplicitInterval && holder.Persistent && holder.InUse));
        Assert.Equal(new[] { "cpu.usage", "memory.usage" }, panel.ItemIds);
        Assert.Equal(new[] { "cpu.usage" }, tray.ItemIds);
        Assert.Equal(at, tray.Since);

        // A renewal keeps the first subscription time and moves only the last-seen time.
        var renewed = at.AddSeconds(5);
        tracker.TrackPersistent("tray", MetricSampleRequest.ForIds(["cpu.usage"]), renewed, TimeSpan.FromSeconds(1));
        tray = Assert.Single(tracker.ReadHolders().Holders, holder => holder.Holder == "tray");
        Assert.Equal(at, tray.Since);
        Assert.Equal(renewed, tray.LastSeen);

        Assert.True(tracker.Remove("panel", renewed));
        var remaining = Assert.Single(Assert.Single(fixture.Owner.Activity.ReadHolders()).Holders);
        Assert.Equal("tray", remaining.Holder);
        Assert.Equal(1000, remaining.IntervalMilliseconds);
    }

    [Fact]
    public void AnonymousReadWithoutIntervalReportsTheRoleDefault()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.Track(request, DateTimeOffset.UtcNow);

        var holder = Assert.Single(tracker.ReadHolders().Holders);
        Assert.Equal(request.CacheKey, holder.Holder);
        Assert.False(holder.ExplicitInterval);
        Assert.False(holder.Persistent);
        Assert.True(holder.InUse);
        Assert.Equal(fixture.Owner.GetRoleSettings(1)!.DefaultIntervalMilliseconds, holder.IntervalMilliseconds);
    }

    [Fact]
    public void ReadingHoldersChangesNoSubscription()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var at = DateTimeOffset.UtcNow;
        tracker.TrackPersistent("tray", MetricSampleRequest.ForIds(["cpu.usage"]), at, TimeSpan.FromSeconds(1));
        tracker.TrackPersistent("panel", MetricSampleRequest.ForIds(["memory.usage"]), at, TimeSpan.FromMilliseconds(500));
        var changes = 0;
        tracker.SubscriptionsChanged += () => changes++;
        var managed = tracker.CaptureManagedSourceState();
        var native = ReadNativeState(fixture);

        var first = tracker.ReadHolders();
        var viaActivity = fixture.Owner.Activity.ReadHolders();
        var second = tracker.ReadHolders();

        Assert.Equal(0, changes);
        Assert.Equal(managed, tracker.CaptureManagedSourceState());
        var after = ReadNativeState(fixture);
        Assert.Equal(native.Sources, after.Sources);
        Assert.Equal(native.Items, after.Items);
        Assert.Equal(first.Holders, second.Holders);
        Assert.Equal(first.Holders, Assert.Single(viaActivity).Holders);
        Assert.True(tracker.HasInUseSubscriptions);
    }

    [Fact]
    public async Task EndpointSerializesSamplingPresentAndKernelHoldersWithProductionJsonMetadata()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        tracker.TrackPersistent("tray", MetricSampleRequest.ForIds(["cpu.usage"]), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        await using var present = PresentFrameEtwTestHarness.Create();
        using var kernel = new KernelEtwSessionBroker(NullLogger<KernelEtwSessionBroker>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(fixture.Owner);
        builder.Services.AddSingleton(present.Source);
        builder.Services.AddSingleton(kernel);
        await using var app = builder.Build();
        app.MapSubscriptionHoldersDebugEndpoint();
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)), Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("/api/debug/subscriptions");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = wire.RootElement;
            var role = Assert.Single(root.GetProperty("sampling").EnumerateArray());
            Assert.Equal(1, role.GetProperty("roleId").GetInt32());
            var holder = Assert.Single(role.GetProperty("holders").EnumerateArray());
            Assert.Equal("tray", holder.GetProperty("holder").GetString());
            Assert.Equal(1000, holder.GetProperty("intervalMilliseconds").GetInt64());
            Assert.Equal("cpu.usage", Assert.Single(holder.GetProperty("itemIds").EnumerateArray()).GetString());
            Assert.Empty(root.GetProperty("present").EnumerateArray());
            Assert.Empty(root.GetProperty("kernel").GetProperty("holders").EnumerateArray());
            Assert.Equal((int)ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
                root.GetProperty("volatility").GetProperty("kind").GetInt32());
        }
        finally { await app.StopAsync(); }
        Assert.Single(tracker.ReadHolders().Holders);
    }

    private static NativeItemSamplingSubscriptionTracker<MetricSampleRequest> CreateMetricTracker(
        HostManagerSamplingSubscriptionTestFixture fixture)
        => new(fixture.Owner, roleId: 1,
            static request => request.CacheKey,
            static request => request.IsCatalogProbe || request.IsEmpty ? [] : request.Ids,
            static (ids, _) => MetricSampleRequest.ForIds(ids),
            coalesceItemsIntoLatestCapture: false);

    private static (NativeSamplingSubscriptionSourceView[] Sources, NativeSamplingSubscriptionItemState[] Items)
        ReadNativeState(HostManagerSamplingSubscriptionTestFixture fixture)
        => fixture.Owner.Execute(1, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (sources[..checked((int)header.SourceOutputCount)], items[..checked((int)header.ItemOutputCount)]);
        });
}
