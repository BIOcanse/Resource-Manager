using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    private static readonly MetricFieldCode[] ClockCodes = [MetricFieldCode.GpuGraphicsClock, MetricFieldCode.GpuMemoryClock];
    private static readonly string[] ClockNames = ["gpu_all_graphics_clock_mhz", "gpu_all_memory_clock_mhz"];

    [Fact]
    public void RawClockLayoutsMatchOfficialHeaderAndKeepTheExistingCommonFields()
    {
        Assert.Equal(80, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.GraphicsClock)).ToInt32());
        Assert.Equal(256, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.MemoryClock)).ToInt32());
        Assert.Equal(1024, Marshal.SizeOf<IntelGclPowerTelemetry>());
        var row = IntelGclReader.Decode(9, Sample(), ClockCodes.ToHashSet());
        Assert.Equal(2, row.Values.Count); Assert.Empty(row.Counters); Assert.Empty(row.Flags);
        Assert.Equal(1222.5, row.Values[MetricFieldCode.GpuGraphicsClock]);
        Assert.Equal(900.75, row.Values[MetricFieldCode.GpuMemoryClock]);
        foreach (var code in ClockCodes)
        {
            var definition = Assert.Single(MetricFieldRegistry.Definitions, field => field.Code == code && field.Vendor == MetricVendor.All);
            Assert.Equal("MHz", definition.Unit); Assert.Contains(definition.Name, ClockNames);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void BadGraphicsClockIsNullWithoutSuppressingValidMemoryClock(int invalid)
    {
        var sdk = new Sdk { ClockMode = invalid };
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(),
            new Dictionary<int, IReadOnlySet<MetricFieldCode>> { [9] = ClockCodes.ToHashSet() }, CancellationToken.None));
        Assert.Null(row.Values[MetricFieldCode.GpuGraphicsClock]);
        Assert.Equal(900.75, row.Values[MetricFieldCode.GpuMemoryClock]);
        Assert.Equal(new IntPtr(1), Assert.Single(sdk.TelemetryDevices)); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void ZeroClocksRemainZeroAndWholeSdkFailureDiscardsDirtyClocks()
    {
        var sdk = new Sdk { Mode = 2 };
        var request = new Dictionary<int, IReadOnlySet<MetricFieldCode>> { [3] = ClockCodes.ToHashSet() };
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.All(row.Values.Values, value => Assert.Equal(0, value));
        sdk.Mode = 1;
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(sdk.Opens, sdk.Closes);
    }

    [Fact]
    public async Task CompiledPlanUsesIntelClocksAndKeepsDedicatedMemoryApplicability()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = CreateClockOwner(slots, secondHasDedicatedMemory: false);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = PublishClockCatalog(owner);
        var bound = dashboard.Current!.FieldBindings.Values.Where(binding => ClockCodes.Contains(binding.Field.Code) && binding.SourceId == "intel.igcl").ToArray();
        Assert.Equal(3, bound.Length);
        Assert.All(bound, binding => { Assert.Equal(MetricVendor.All, binding.Field.Vendor); Assert.Equal("MHz", binding.Field.Unit); });
        var identities = RecordedGpuIdentities(secondHasDedicatedMemory: false).ToDictionary(static gpu => gpu.DisplayIndex);
        Assert.Contains(bound, binding => binding.Field.Code == MetricFieldCode.GpuMemoryClock && binding.DeviceIdentity == identities[9].ExactIdentityKey);
        Assert.Contains(bound, binding => binding.Field.Code == MetricFieldCode.GpuGraphicsClock && binding.DeviceIdentity == identities[3].ExactIdentityKey);
        Assert.DoesNotContain(bound, binding => binding.Field.Code == MetricFieldCode.GpuMemoryClock && binding.DeviceIdentity == identities[3].ExactIdentityKey);
    }

    [Fact]
    public async Task CommonClocksReachOriginalCacheHttpAndPushAndStopReadingWhenUnsubscribed()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = CreateClockOwner(slots, secondHasDedicatedMemory: true);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = PublishClockCatalog(owner);
        var gpuIdentity = RecordedGpuIdentities().Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey;
        var device = dashboard.Current!.FieldBindings.Values.Single(binding => binding.SourceId == "intel.igcl" &&
            binding.Field.Code == MetricFieldCode.GpuGraphicsClock && binding.DeviceIdentity == gpuIdentity).DeviceIdentity;
        var sdk = new Sdk(); var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds
            .Select(id => id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No system memory demand"), () => throw new InvalidOperationException("No system memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, new WindowsGpuAdapterOrderMonitoringZone(Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelGclZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, ClockNames);
        Dictionary<string, DeviceMetricFieldValue> Fields(HardwareMetricSnapshot snapshot)
            => Assert.Single(MetricDeviceFields.Read(snapshot, device, ClockNames.ToHashSet())).Fields.ToDictionary(field => field.Name);
        await using var app = await StartIntelHttpAsync(sampler, dashboard);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + string.Join(',', ClockNames);
        Assert.All(Fields(await sampler.GetSnapshotAsync(request, timeout.Token)).Values, field => Assert.Null(field.NumericValue));
        using (var cold = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
            Assert.All(cold.RootElement[0].GetProperty("fields").EnumerateArray(), field => Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind));
        Assert.Equal(0, sdk.Opens);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("igcl-clocks", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[ClockNames[0]].NumericValue != 1222.5);
                Assert.Equal(900.75, Fields(stream.Current)[ClockNames[1]].NumericValue);
                Volatile.Write(ref sdk.ClockMode, 2);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[ClockNames[0]].NumericValue is not null);
                Assert.Equal(900.75, Fields(stream.Current)[ClockNames[1]].NumericValue);
                Volatile.Write(ref sdk.Mode, 2); Volatile.Write(ref sdk.ClockMode, 0);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[ClockNames[0]].NumericValue != 0);
                Assert.Equal(0, Fields(stream.Current)[ClockNames[1]].NumericValue);
                using (var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
                    CheckClockWire(wire.RootElement[0].GetProperty("fields"));
                using var cancelPush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "igcl-clock-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, cancelPush.Token);
                response.EnsureSuccessStatusCode(); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancelPush.Token));
                using var json = JsonDocument.Parse((await reader.ReadLineAsync(cancelPush.Token))!);
                CheckClockWire(json.RootElement.GetProperty("value")[0].GetProperty("fields"));
                await cancelPush.CancelAsync();
            }
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)
                typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var sampleGate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await sampleGate.WaitAsync(timeout.Token); sampleGate.Release();
            var calls = Volatile.Read(ref sdk.TelemetryCalls);
            using (var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
                CheckClockWire(wire.RootElement[0].GetProperty("fields"));
            await Task.Delay(200, timeout.Token); Assert.Equal(calls, Volatile.Read(ref sdk.TelemetryCalls));
            lock (sdk.TelemetryDevices) Assert.All(sdk.TelemetryDevices, handle => Assert.Equal(new IntPtr(1), handle));
            await sampler.StopAsync(CancellationToken.None);
            Assert.All(Fields(await sampler.GetSnapshotAsync(request, timeout.Token)).Values, field => Assert.Equal(0, field.NumericValue));
            Assert.Equal(sdk.Opens, sdk.Closes);
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static HostManagerMetricSnapshotOwner CreateClockOwner(HostManagerSamplingSubscriptionTestFixture slots, bool secondHasDedicatedMemory)
        => new(slots.Provider, new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete,
                RecordedGpuIdentities(secondHasDedicatedMemory),
                NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])),
            new TestEnvironment(Path.Combine(Path.GetTempPath(), $"igcl-clock-worker-{Guid.NewGuid():N}")), NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);

    private static DashboardMonitoringCatalogState PublishClockCatalog(HostManagerMetricSnapshotOwner owner)
    {
        var dashboard = new DashboardMonitoringCatalogState();
        using var lease = owner.Acquire();
        dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        return dashboard;
    }

    private static void CheckClockWire(JsonElement fields)
    {
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Equal(ClockNames.Order(), fields.EnumerateArray().Select(field => field.GetProperty("name").GetString()!).Order());
        Assert.All(fields.EnumerateArray(), field =>
        {
            Assert.Equal("intel.igcl", field.GetProperty("sourceId").GetString()); Assert.Equal("MHz", field.GetProperty("unit").GetString());
            Assert.Equal("float64", field.GetProperty("valueKind").GetString()); Assert.Equal(0, field.GetProperty("numericValue").GetDouble());
            Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
        });
    }
}
