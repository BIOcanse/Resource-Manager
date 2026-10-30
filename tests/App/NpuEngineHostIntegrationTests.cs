using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class NpuEngineHostServiceGraphTests
{
    [Fact]
    public void NormalProfileRegistersOnePassiveNpuEngineZone()
    {
        var registrations = new ServiceCollection(); registrations.AddLogging();
        registrations.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registrations, row => row.ServiceType == typeof(WindowsNpuEngineMonitoringZone)).Lifetime);
        using var services = registrations.BuildServiceProvider();
        Assert.Same(services.GetRequiredService<WindowsNpuEngineMonitoringZone>(), services.GetRequiredService<WindowsNpuEngineMonitoringZone>());
    }
}

public sealed partial class NvidiaFanHttpIntegrationTests
{
    [Fact]
    public async Task NpuEngineNormalHostHttpAndUnifiedStreamShareNativeRatioAndOriginalCurrent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var recording = new NpuEngineRecordedResources(); using var resources = recording.Create();
        var topology = new WindowsHardwareMetricTopologySource(new(resources.ReadGpuInventory, resources.ReadStorageIdentities,
            resources.ReadFanIdentities, resources.ReadBatteryIdentities, resources.ReadNetworkIdentities, resources.ReadNumaIdentities)
            { OpenNpuEngineDirectory = resources.OpenNpuEngineDirectory, ReadNpuMemoryIdentities = resources.ReadNpuMemoryIdentities }, () => null);
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource((memory, fan, prior, engine) => topology.Capture(memory, fan, prior, engine)),
            new TestEnvironment(Path.Combine(AppContext.BaseDirectory, "npu-engine-host", Guid.NewGuid().ToString("N"))),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        using var workspaceHolding = owner.Hold();
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire()) dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var zone = new WindowsNpuEngineMonitoringZone(recording.Open, recording.Read);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, 
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, npuEngineZone: zone);
        using var dependencyHttp = new HttpClient(); var controls = new UnusedControls();
        var core = new ReadWriteCore(new HostReadWriteBackend(sampler, sampler, dashboard, controls, controls), new DependencyActions(dependencyHttp, []));
        var selected = recording.Opened[0].Identities[0];
        var identity = selected.ExactIdentityKey;
        Assert.Equal("npu_engine", core.Catalog.Devices.Single(device => device.Identity == identity).Resource);
        Assert.Empty(core.Query(new(identity, NpuEngineRecordedResources.Ratio)).Read.Dependencies);
        var busyField = Assert.Single(core.Catalog.Fields, field =>
            field.Field.FieldName == NpuEngineRecordedResources.Busy).Field;
        var busyQuery = "?device=" + Uri.EscapeDataString(busyField.DeviceIdentity)
            + "&fields=" + NpuEngineRecordedResources.Busy;
        var busyCapability = core.Query(busyField);
        Assert.True(busyCapability.Read.Supported); Assert.False(busyCapability.Write.Supported);
        Assert.Equal("windows.npu-engine", busyCapability.ReadSource);
        var query = "?device=" + Uri.EscapeDataString(identity) + "&fields=" + NpuEngineRecordedResources.Raw + "," + NpuEngineRecordedResources.Ratio;
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHttpAsync(sampler, dashboard, slots.Provider, core, ended, timeout.Token);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)), Timeout = Timeout.InfiniteTimeSpan };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        Assert.Equal(0, recording.Reads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            using (var initial = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
            {
                var rawField = Assert.Single(Assert.Single(initial.RootElement.EnumerateArray()).GetProperty("fields").EnumerateArray(),
                    field => field.GetProperty("name").GetString() == NpuEngineRecordedResources.Raw);
                Assert.Equal(JsonValueKind.String, rawField.GetProperty("integerValue").ValueKind);
            }
            Assert.True(recording.Reads > 0);
            var beforeBusy = recording.Reads;
            using (var initialBusy = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + busyQuery, timeout.Token))
            {
                var initialDevice = Assert.Single(initialBusy.RootElement.EnumerateArray());
                Assert.Equal(busyField.DeviceIdentity, initialDevice.GetProperty("deviceIdentity").GetString());
                var field = Assert.Single(initialDevice.GetProperty("fields").EnumerateArray());
                Assert.Equal(NpuEngineRecordedResources.Busy, field.GetProperty("name").GetString());
                Assert.Equal("windows.npu-engine", field.GetProperty("sourceId").GetString());
            }
            Assert.True(recording.Reads > beforeBusy);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "npu-engine", path = "/api/metrics/subscribe" + query + "&intervalMs=100" },
                  new { id = "npu-busy", path = "/api/metrics/subscribe" + busyQuery + "&intervalMs=100" } } }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var value = await ReadSubscriptionUntilAsync(reader, "npu-engine", json => EngineRatio(json) is { } field
                && field.GetProperty("numericValue").ValueKind == JsonValueKind.Number
                && field.GetProperty("numericValue").GetDouble() == 125d, timeout.Token);
            Assert.Equal(125d, EngineRatio(value)!.Value.GetProperty("numericValue").GetDouble());
            var device = Assert.Single(value.EnumerateArray()); Assert.Equal(identity, device.GetProperty("deviceIdentity").GetString());
            var raw = device.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == NpuEngineRecordedResources.Raw);
            Assert.Equal("unsigned64", raw.GetProperty("valueKind").GetString()); Assert.Equal("µs", raw.GetProperty("unit").GetString());
            Assert.Equal(JsonValueKind.String, raw.GetProperty("integerValue").ValueKind); Assert.Equal(JsonValueKind.Null, raw.GetProperty("numericValue").ValueKind);
            Assert.Equal("windows.npu-engine", raw.GetProperty("sourceId").GetString());
            var busyValue = await ReadSubscriptionUntilAsync(reader, "npu-busy", json => EngineBusy(json) is { } field
                && field.GetProperty("numericValue").ValueKind == JsonValueKind.Number
                && field.GetProperty("numericValue").GetDouble() == 125d, timeout.Token);
            var busyDevice = Assert.Single(busyValue.EnumerateArray());
            Assert.Equal(busyField.DeviceIdentity, busyDevice.GetProperty("deviceIdentity").GetString());
            var busyJson = Assert.Single(busyDevice.GetProperty("fields").EnumerateArray());
            Assert.Equal(NpuEngineRecordedResources.Busy, busyJson.GetProperty("name").GetString());
            Assert.Equal("%", busyJson.GetProperty("unit").GetString());
            Assert.Equal("windows.npu-engine", busyJson.GetProperty("sourceId").GetString());
            Assert.Equal(125d, busyJson.GetProperty("numericValue").GetDouble());
            var requests = recording.RequestHistory.ToArray();
            Assert.Equal(recording.Opened[0].Identities.Select(static target => target.ExactIdentityKey).Order(StringComparer.Ordinal),
                requests.SelectMany(static request => request.Targets).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            Assert.All(requests, static request => Assert.Equal(256U, request.Maximum));
            await cancellation.CancelAsync(); response.Dispose(); reader.Dispose(); await ended.Task.WaitAsync(timeout.Token);
            var beforeFetch = recording.Reads;
            using var fresh = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token);
            Assert.Equal(125d, EngineRatio(fresh.RootElement)!.Value.GetProperty("numericValue").GetDouble());
            Assert.True(recording.Reads > beforeFetch);
            var beforeBusyFetch = recording.Reads;
            using var freshBusy = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + busyQuery, timeout.Token);
            Assert.Equal(125d, EngineBusy(freshBusy.RootElement)!.Value.GetProperty("numericValue").GetDouble());
            Assert.True(recording.Reads > beforeBusyFetch);
            await sampler.StopAsync(timeout.Token);
            var reads = recording.Reads; var current = core.ReadCached(new(identity, NpuEngineRecordedResources.Ratio));
            Assert.Equal(current, core.ReadCached(new(identity, NpuEngineRecordedResources.Ratio))); Assert.Equal(reads, recording.Reads);
            var incarnation = owner.CurrentWorkspaceIdentity;
            owner.RequestTopologyRefresh(); Assert.NotNull(owner.RefreshTopologyIfRequested());
            Assert.Equal(incarnation, owner.CurrentWorkspaceIdentity); Assert.Equal(1, recording.Opens);
            using var retired = owner.Acquire();
            recording.Opened[0].Current = false; owner.RequestTopologyRefresh();
            Assert.NotNull(owner.RefreshTopologyIfRequested()); Assert.NotEqual(incarnation, owner.CurrentWorkspaceIdentity);
            Assert.False(recording.Opened[0].Disposed);
            var published = false; Assert.False(retired.TryPublish(_ => { published = true; return true; }, out _)); Assert.False(published);
            retired.Dispose(); Assert.True(recording.Opened[0].Disposed);
        }
        finally
        { await sampler.StopAsync(CancellationToken.None); await app.StopAsync(CancellationToken.None); workspaceHolding.Dispose(); }

        static JsonElement? EngineRatio(JsonElement json)
        {
            foreach (var device in json.EnumerateArray()) foreach (var field in device.GetProperty("fields").EnumerateArray())
                if (field.GetProperty("name").GetString() == NpuEngineRecordedResources.Ratio) return field;
            return null;
        }

        static JsonElement? EngineBusy(JsonElement json)
        {
            foreach (var device in json.EnumerateArray()) foreach (var field in device.GetProperty("fields").EnumerateArray())
                if (field.GetProperty("name").GetString() == NpuEngineRecordedResources.Busy) return field;
            return null;
        }

        static async Task<JsonElement> ReadSubscriptionUntilAsync(StreamReader reader, string subscriptionId,
            Func<JsonElement, bool> accept, CancellationToken token)
        {
            for (var index = 0; index < 128; index++)
            {
                var line = await reader.ReadLineAsync(token);
                Assert.NotNull(line);
                using var document = JsonDocument.Parse(line!);
                if (!string.Equals(document.RootElement.GetProperty("subscriptionId").GetString(), subscriptionId,
                    StringComparison.Ordinal))
                    continue;
                var value = document.RootElement.GetProperty("value");
                if (accept(value)) return value.Clone();
            }
            throw new InvalidOperationException($"The real channel did not publish the expected '{subscriptionId}' state within its bounded frame count.");
        }
    }
}
