using RecordedIntelFanResources = Resource_Manager_APP.Tests.IntelFanOwnerHostIntegrationTests.RecordedIntelFanResources;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

// Reuse the existing real Host HTTP server/routes and unified-channel lifecycle helpers.
public sealed partial class NvidiaFanHttpIntegrationTests
{
    [Theory]
    [InlineData(0U, 0d)]
    [InlineData(1U, 1450d)]
    public async Task NormalIntelFanHttpCatalogCacheAndUnifiedPushUseTheOriginalHostCurrent(uint fanIndex, double rpm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var recording = new RecordedIntelFanResources();
        using var resources = recording.Create();
        var topology = new WindowsHardwareMetricTopologySource(new(resources.ReadGpuInventory, resources.ReadStorageIdentities,
            resources.ReadFanIdentities, resources.ReadBatteryIdentities, resources.ReadNetworkIdentities, resources.ReadNumaIdentities)
            { OpenIgclFanDirectory = resources.OpenIgclFanDirectory, ReadNvmlFanIdentities = resources.ReadNvmlFanIdentities }, () => null);
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource((npu, intel, prior) => topology.Capture(npu, intel, prior)),
            new TestEnvironment(Path.Combine(AppContext.BaseDirectory, "intel-fan-http", Guid.NewGuid().ToString("N"))),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(timeout.Token);
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));

        var selectedFan = recording.Fans.Single(fan => fan.FanIndex == fanIndex);
        var queriedTargets = new ConcurrentQueue<IntelGclFanIdentity>();
        var failedQuery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredQuery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0;
        var queryCount = 0;
        var zone = new IntelGclMonitoringZone((_, _, _) => throw new InvalidOperationException("No whole-GPU Intel telemetry was selected."),
            (_, _) => throw new InvalidOperationException("This HTTP fixture uses its recorded original topology directory."),
            (targets, inventory, maximum, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(16U, maximum);
                Assert.Equal(0x8086U, Assert.Single(inventory.Adapters).VendorId);
                var target = Assert.Single(targets);
                Assert.Same(selectedFan, target);
                queriedTargets.Enqueue(target);
                Interlocked.Increment(ref queryCount);
                var currentPhase = Volatile.Read(ref phase);
                if (currentPhase == 1)
                {
                    failedQuery.TrySetResult(true);
                    return [new IntelGclFanReading(target, null)];
                }
                if (currentPhase == 2) recoveredQuery.TrySetResult(true);
                return [new IntelGclFanReading(target, checked((int)rpm))];
            });
        var registry = new MonitoringSourceZoneRegistry([new WindowsMemoryMonitoringZone(), zone]);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!,
            new WindowsGpuAdapterOrderMonitoringZone(RecordedIntelFanResources.Inventory), null!, null!, null!, null!,
            null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance,
            intelGclZone: zone, borrowedControls: resources.Controls);
        using var dependencyHttp = new HttpClient();
        var controls = new UnusedControls();
        var backend = new HostReadWriteBackend(sampler, sampler, dashboard, controls, controls);
        var core = new ReadWriteCore(backend, new DependencyActions(dependencyHttp, []));
        var publicDevice = core.Catalog.Devices.Single(device => device.Identity == selectedFan.ExactIdentityKey);
        Assert.Equal("gpu_fan", publicDevice.Resource);
        Assert.Equal("intel", publicDevice.Vendor);
        var channelEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHttpAsync(sampler, dashboard, slots.Provider, core, channelEnded, timeout.Token);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = Timeout.InfiniteTimeSpan
        };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        var query = "?device=" + Uri.EscapeDataString(selectedFan.ExactIdentityKey) + "&fields=" + RecordedIntelFanResources.Name;
        var selection = MetricDeviceFields.CreateRequest(dashboard.Current!, selectedFan.ExactIdentityKey, [RecordedIntelFanResources.Name]);
        await AssertPublicIntelFanCatalogsAsync(client, recording, selectedFan, timeout.Token);
        using (var initial = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
            Assert.Equal(JsonValueKind.Null, IntelHttpField(initial.RootElement, selectedFan).GetProperty("numericValue").ValueKind);
        using (var generic = await client.GetAsync("/api/metrics/snapshot?ids=" + Uri.EscapeDataString(selection.Ids.First()), timeout.Token))
            Assert.Equal(HttpStatusCode.OK, generic.StatusCode);
        Assert.Equal(0, Volatile.Read(ref queryCount));

        await sampler.StartAsync(timeout.Token);
        try
        {
            using var channelCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                    { new { id = "fan", path = "/api/metrics/subscribe" + query + "&intervalMs=100" } } })
            };
            using var response = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, channelCancellation.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var current = await ReadUntilAsync(reader, value => IntelHttpHasRpm(value, selectedFan, rpm), timeout.Token);
            AssertIntelHttpRpm(current, selectedFan, rpm);
            Assert.True(Volatile.Read(ref queryCount) > 0);
            using (var cached = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
                AssertIntelHttpRpm(cached.RootElement, selectedFan, rpm);

            Volatile.Write(ref phase, 1);
            await failedQuery.Task.WaitAsync(timeout.Token);
            var failed = await ReadUntilAsync(reader, value => IntelHttpField(value, selectedFan) is var field
                && field.GetProperty("numericValue").ValueKind == JsonValueKind.Null
                && field.GetProperty("status").GetInt32() == (int)SamplingObservationStatus.Unavailable, timeout.Token);
            AssertIntelHttpRpm(failed, selectedFan, null);
            using (var cleared = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
                AssertIntelHttpRpm(cleared.RootElement, selectedFan, null);
            await AssertPublicIntelFanCatalogsAsync(client, recording, selectedFan, timeout.Token);

            Volatile.Write(ref phase, 2);
            await recoveredQuery.Task.WaitAsync(timeout.Token);
            var recovered = await ReadUntilAsync(reader, value => IntelHttpHasRpm(value, selectedFan, rpm), timeout.Token);
            AssertIntelHttpRpm(recovered, selectedFan, rpm);
            await channelCancellation.CancelAsync();
            response.Dispose();
            reader.Dispose();
            await channelEnded.Task.WaitAsync(timeout.Token);
            await sampler.StopAsync(timeout.Token);
            Assert.Null(sampler.CaptureSamplingWorkerTask());
            var drainedReads = Volatile.Read(ref queryCount);
            var originalCurrent = sampler.ReadLatest(selection)!;
            var reference = new FieldReference(selectedFan.ExactIdentityKey, RecordedIntelFanResources.Name);
            var originalReading = core.ReadCached(reference);
            Assert.Equal(rpm, originalReading.Value!.Number);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cached = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token);
                AssertIntelHttpRpm(cached.RootElement, selectedFan, rpm);
                Assert.Equal(originalReading, core.ReadCached(reference));
                Assert.Equal(originalCurrent.CommittedGeneration, sampler.ReadLatest(selection)!.CommittedGeneration);
                Assert.Equal(drainedReads, Volatile.Read(ref queryCount));
            }
            Assert.All(queriedTargets, target => Assert.Same(selectedFan, target));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            await app.StopAsync(CancellationToken.None);
            await owner.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertPublicIntelFanCatalogsAsync(HttpClient client, RecordedIntelFanResources recording,
        IntelGclFanIdentity selected, CancellationToken token)
    {
        using var catalog = await GetJsonAsync(client, "/api/metrics/fields/catalog", token);
        var fans = catalog.RootElement.EnumerateArray().Where(device => device.GetProperty("fields").EnumerateArray()
            .Any(field => field.GetProperty("name").GetString() == RecordedIntelFanResources.Name)).ToArray();
        Assert.Equal(recording.Fans.Select(fan => fan.ExactIdentityKey).Order(),
            fans.Select(device => device.GetProperty("deviceIdentity").GetString()!).Order());
        Assert.Equal(2, fans.Length);
        Assert.All(fans, device =>
        {
            var field = Assert.Single(device.GetProperty("fields").EnumerateArray());
            Assert.Equal((int)MetricResource.Fan, field.GetProperty("resource").GetInt32());
            Assert.Equal((int)MetricVendor.Intel, field.GetProperty("vendor").GetInt32());
            Assert.Equal("RPM", field.GetProperty("unit").GetString());
            Assert.False(field.TryGetProperty("meaning", out _));
        });
        using var capabilityCatalog = await GetJsonAsync(client, "/api/metrics/fields/capabilities", token);
        var capability = Assert.Single(capabilityCatalog.RootElement.EnumerateArray().Where(item =>
            item.GetProperty("field").GetProperty("deviceIdentity").GetString() == selected.ExactIdentityKey
                && item.GetProperty("field").GetProperty("fieldName").GetString() == RecordedIntelFanResources.Name));
        Assert.Equal("intel.igcl", capability.GetProperty("readSource").GetString());
        Assert.True(capability.GetProperty("read").GetProperty("supported").GetBoolean());
        Assert.False(capability.GetProperty("write").GetProperty("supported").GetBoolean());
        Assert.Equal("float64", capability.GetProperty("definition").GetProperty("valueKind").GetString());
        Assert.Equal("RPM", capability.GetProperty("definition").GetProperty("unit").GetString());
        using var generic = await client.GetAsync("/api/metrics/catalog", token);
        Assert.Equal(HttpStatusCode.OK, generic.StatusCode);
    }

    private static JsonElement IntelHttpField(JsonElement value, IntelGclFanIdentity selected)
    {
        var device = Assert.Single(value.EnumerateArray());
        Assert.Equal(selected.ExactIdentityKey, device.GetProperty("deviceIdentity").GetString());
        var field = Assert.Single(device.GetProperty("fields").EnumerateArray());
        Assert.Equal(RecordedIntelFanResources.Name, field.GetProperty("name").GetString());
        return field;
    }

    private static bool IntelHttpHasRpm(JsonElement value, IntelGclFanIdentity selected, double rpm)
    {
        var field = IntelHttpField(value, selected);
        return field.GetProperty("numericValue").ValueKind == JsonValueKind.Number
            && field.GetProperty("numericValue").GetDouble() == rpm
            && field.GetProperty("status").GetInt32() == (int)SamplingObservationStatus.Current;
    }

    private static void AssertIntelHttpRpm(JsonElement value, IntelGclFanIdentity selected, double? rpm)
    {
        var field = IntelHttpField(value, selected);
        Assert.Equal("intel.igcl", field.GetProperty("sourceId").GetString());
        Assert.Equal("RPM", field.GetProperty("unit").GetString());
        Assert.Equal("float64", field.GetProperty("valueKind").GetString());
        Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
        if (rpm is { } speed)
        {
            Assert.Equal(speed, field.GetProperty("numericValue").GetDouble());
            Assert.Equal((int)SamplingObservationStatus.Current, field.GetProperty("status").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, field.GetProperty("observedAt").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind);
            Assert.Equal((int)SamplingObservationStatus.Unavailable, field.GetProperty("status").GetInt32());
            Assert.Equal(JsonValueKind.Null, field.GetProperty("observedAt").ValueKind);
        }
    }
}
