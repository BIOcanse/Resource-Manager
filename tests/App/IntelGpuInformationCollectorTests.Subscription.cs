using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Application.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    [Fact]
    public void ProductionZoneRegistryIncludesTheSameIntelSourceUsedByTheSampler()
    {
        var sdk = new Sdk(); var intel = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var zones = MonitoringSourceZoneCatalog.CreateDefaultZones(AppContext.BaseDirectory)
            .Select(zone => zone.DisplayName == intel.DisplayName ? intel : zone).ToArray();
        var services = new ServiceCollection(); foreach (var zone in zones) services.AddSingleton(zone.GetType(), zone);
        using var provider = services.BuildServiceProvider();
        var method = typeof(ResourceManagerServiceCollectionExtensions).GetMethod("GetMonitoringSourceZones", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = Assert.IsAssignableFrom<IReadOnlyList<MonitoringSourceZone>>(method.Invoke(null, [provider]));
        Assert.Equal(MonitoringSourceZoneCatalog.SourceIds.Order(), actual.Select(zone => zone.DisplayName).Order());
        Assert.Same(intel, Assert.Single(actual, zone => zone.DisplayName == MonitoringSourceZoneIds.VendorIntelGcl));
        Assert.Equal(0, sdk.Opens);
    }

    [Fact]
    public async Task OriginalWorkerPublishesAllTypedFieldsThroughHttpAndPushAndStopsAfterCancellation()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var root = Path.Combine(Path.GetTempPath(), $"igcl-worker-{Guid.NewGuid():N}");
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider, new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete,
                RecordedGpuIdentities(),
                NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])),
            new TestEnvironment(root), NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var bound = dashboard.Current!.FieldBindings.Values.Where(binding => binding.Field.Vendor == MetricVendor.Intel && IntelGclReader.IsRawTelemetryField(binding.Field.Code)).ToArray();
        Assert.Equal(36, bound.Length); Assert.All(bound, binding => Assert.Equal("intel.igcl", binding.SourceId));
        var gpuIdentity = RecordedGpuIdentities().Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey;
        var device = bound.Single(binding => binding.Field.Code == MetricFieldCode.IgclGpuEnergy && binding.DeviceIdentity == gpuIdentity).DeviceIdentity;
        var sdk = new Sdk(); var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds
            .Select(id => id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No system memory demand"), () => throw new InvalidOperationException("No system memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, new WindowsGpuAdapterOrderMonitoringZone(Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelGclZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, Names);
        await using var app = await StartIntelHttpAsync(sampler, dashboard);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + string.Join(',', Names);
        Assert.All(ReadIntelFields(sampler.ReadLatest(request) ?? dashboard.Current!, device).Values, field =>
        { Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); Assert.Null(field.BooleanValue); });
        Assert.Equal(0, sdk.Opens);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("igcl-fields", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadIntelFields(stream.Current, device)["gpu_intel_gpu_energy_joule"].NumericValue != 5);
                var current = ReadIntelFields(stream.Current, device);
                Assert.All(current.Values, field => Assert.Equal(SamplingObservationStatus.Current, field.Status));
                Assert.False(current["gpu_intel_power_limited"].BooleanValue); Assert.True(current["gpu_intel_temperature_limited"].BooleanValue);
                Assert.Equal(MetricInteger.Parse("9007199254740993"), current["gpu_intel_memory_read_counter_bytes"].IntegerValue);
                Assert.Equal(MetricInteger.Parse("18446744073709551615"), current["gpu_intel_memory_write_counter_bytes"].IntegerValue);
                Assert.Equal(-1, current["gpu_intel_core_temperature_celsius"].NumericValue);
                lock (sdk.TelemetryDevices) Assert.All(sdk.TelemetryDevices, handle => Assert.Equal(new IntPtr(1), handle));
                Volatile.Write(ref sdk.Mode, 1);
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadIntelFields(stream.Current, device)["gpu_intel_gpu_energy_joule"].NumericValue is not null);
                Assert.All(ReadIntelFields(stream.Current, device).Values, field =>
                { Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); Assert.Null(field.BooleanValue); });
                Volatile.Write(ref sdk.Mode, 2);
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadIntelFields(stream.Current, device)["gpu_intel_gpu_energy_joule"].NumericValue != 0);
                using (var currentWire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
                    CheckIntelWire(currentWire.RootElement[0].GetProperty("fields"));
                using var cancelPush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "igcl-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, cancelPush.Token);
                response.EnsureSuccessStatusCode(); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancelPush.Token));
                await ReadIntelPushAsync(reader, cancelPush.Token); await cancelPush.CancelAsync();
            }
            // Client socket cancellation is asynchronous. Wait for both existing
            // registrations to leave, then drain a capture already in progress.
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)
                typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var sampleGate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler)
                .GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await sampleGate.WaitAsync(timeout.Token); sampleGate.Release();
            var calls = Volatile.Read(ref sdk.TelemetryCalls);
            using (var freshWire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
                CheckIntelWire(freshWire.RootElement[0].GetProperty("fields"));
            Assert.True(Volatile.Read(ref sdk.TelemetryCalls) > calls);
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            await sampleGate.WaitAsync(timeout.Token); sampleGate.Release();
            var afterFetch = Volatile.Read(ref sdk.TelemetryCalls);
            await Task.Delay(200, timeout.Token); Assert.Equal(afterFetch, Volatile.Read(ref sdk.TelemetryCalls));
            await sampler.StopAsync(CancellationToken.None);
            Assert.Equal(0, ReadIntelFields(sampler.ReadLatest(request)!, device)["gpu_intel_gpu_energy_joule"].NumericValue);
            Assert.Equal(sdk.Opens, sdk.Closes);
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static Dictionary<string, DeviceMetricFieldValue> ReadIntelFields(HardwareMetricSnapshot snapshot, string device)
        => Assert.Single(MetricDeviceFields.Read(snapshot, device, Names.ToHashSet())).Fields.ToDictionary(field => field.Name);
    private static void CheckIntelWire(JsonElement fields)
    {
        Assert.Equal(18, fields.GetArrayLength()); var byName = fields.EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        Assert.All(byName.Values, field => Assert.Equal("intel.igcl", field.GetProperty("sourceId").GetString()));
        var energy = byName["gpu_intel_gpu_energy_joule"]; Assert.Equal("J", energy.GetProperty("unit").GetString());
        Assert.Equal(0, energy.GetProperty("numericValue").GetDouble());
        var bytes = byName["gpu_intel_memory_read_counter_bytes"]; Assert.Equal("B", bytes.GetProperty("unit").GetString());
        Assert.Equal("unsigned64", bytes.GetProperty("valueKind").GetString()); Assert.Equal("0", bytes.GetProperty("integerValue").GetString());
        Assert.Equal(JsonValueKind.Null, bytes.GetProperty("numericValue").ValueKind);
        var limited = byName["gpu_intel_power_limited"]; Assert.Equal("boolean", limited.GetProperty("valueKind").GetString());
        Assert.False(limited.GetProperty("booleanValue").GetBoolean()); Assert.Equal(JsonValueKind.Null, limited.GetProperty("numericValue").ValueKind);
    }
    private static async Task ReadIntelPushAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken); Assert.NotNull(line);
        using var json = JsonDocument.Parse(line);
        CheckIntelWire(json.RootElement.GetProperty("value")[0].GetProperty("fields"));
    }
    private static async Task<WebApplication> StartIntelHttpAsync(WindowsHardwareMetricSampler sampler, HardwareMetricCatalogState catalog)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read");
        builder.Services.AddSingleton(catalog); builder.Services.AddSingleton<IMetricSampler>(sampler); builder.Services.AddSingleton<IMetricSnapshotPushSource>(sampler);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var name in new[] { "MapMetricFieldEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync(); return app;
    }
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests"; public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
