using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    private static JsonElement Field(JsonElement snapshot, string identity, string name) =>
        Assert.Single(Assert.Single(snapshot.GetProperty("devices").EnumerateArray(),
            device => device.GetProperty("deviceIdentity").GetString() == identity)
            .GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("definition").GetProperty("name").GetString() == name);

    private static async Task<WebApplication> StartAsync(DeviceTopologySnapshotProvider provider)
    {
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(provider);
        builder.Services.AddHostedService(_ => provider);
        var app = builder.Build();
        foreach (var method in new[] { "MapDeviceTopologyEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
    {
        BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(15)
    }, app.Services);

    private sealed class Reader : IDeviceTopologyReader
    {
        private readonly Queue<object> results;
        private readonly Func<DeviceTopologySnapshot>? read;
        public Reader(params object[] sequence) => results = new(sequence);
        public Reader(Func<DeviceTopologySnapshot> read) { this.read=read; results=[]; }
        private int readCount;
        public int ReadCount => Volatile.Read(ref readCount);
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Interlocked.Increment(ref readCount);
            if (read is not null) return read();
            var next = results.Count > 1 ? results.Dequeue() : results.Peek();
            return next is Exception error ? throw error : (DeviceTopologySnapshot)next;
        }
    }

    private sealed class Store(DeviceTopologyPersistedSnapshot? persisted = null) : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult(persisted);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostManagerSamplingSubscriptionTestFixture sampling = new();
        public DeviceTopologySnapshotProvider Provider { get; }
        public Fixture(Reader reader,DeviceTopologyPersistedSnapshot? persisted=null) => Provider = new(reader, new Store(persisted), new(), sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance,
            new WindowsAcpiFirmwareTableReader());
        internal async Task WaitForNoDemandAndIdleAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var idle = sampling.Owner.Execute(2, session =>
                {
                    var header = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader
                    {
                        AbiVersion = ResourceManager.Subscription.Native.NativeSamplingSubscriptionAbi.Version,
                        StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<
                            ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader>())
                    };
                    var sources = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSourceView[
                        session.Capacity.SnapshotSourceCapacity];
                    var items = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionItemState[
                        session.Capacity.SnapshotItemCapacity];
                    Assert.Equal(ResourceManager.Subscription.Native.NativeSamplingSubscriptionStatus.Ok,
                        session.Snapshot(ref header, sources, items));
                    // Removal retires native source/membership demand. A previously issued
                    // synchronous read may still be completing, so also await its settlement.
                    return header.ActiveSourceCount == 0 && header.PersistentSourceCount == 0
                        && header.MembershipCount == 0
                        && items.Take(checked((int)header.ItemOutputCount))
                            .All(static item => item.LastPlannedEpoch == 0);
                });
                if (idle) return;
                await Task.Delay(10, deadline.Token);
            }
        }

        public void Dispose() { Provider.Dispose(); sampling.Dispose(); }
    }
}
