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

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    private static readonly MetricFieldCode[] PciCapabilityCodes = [MetricFieldCode.IgclPciMaximumGeneration, MetricFieldCode.IgclPciMaximumWidth];
    private static readonly string[] PciCapabilityNames = ["gpu_intel_pcie_maximum_generation", "gpu_intel_pcie_maximum_width_lanes"];
    private static Dictionary<int, IReadOnlySet<MetricFieldCode>> PciCapabilityRequest()
        => new() { [9] = PciCapabilityCodes.ToHashSet() };

    [Fact]
    public void PciPropertiesLayoutsAndSelectedCapabilityReadMatchTheOfficialHeader()
    {
        Assert.Equal(24, Marshal.SizeOf<IntelGclPciAddress>()); Assert.Equal(64, Marshal.SizeOf<IntelGclPciProperties>());
        Assert.Equal(8, Marshal.OffsetOf<IntelGclPciProperties>(nameof(IntelGclPciProperties.Address)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<IntelGclPciProperties>(nameof(IntelGclPciProperties.MaximumSpeed)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<IntelGclPciProperties>(nameof(IntelGclPciProperties.ResizableBarSupported)).ToInt32());
        Assert.Equal(57, Marshal.OffsetOf<IntelGclPciProperties>(nameof(IntelGclPciProperties.ResizableBarEnabled)).ToInt32());
        var sdk = new Sdk(); var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), PciCapabilityRequest(), CancellationToken.None));
        Assert.Equal(2, row.Values.Count); Assert.Equal(5, row.Values[PciCapabilityCodes[0]]); Assert.Equal(16, row.Values[PciCapabilityCodes[1]]);
        Assert.Equal(new IntPtr(1), Assert.Single(sdk.PciPropertiesDevices)); Assert.Equal(0, sdk.PciCalls); Assert.Equal(0, sdk.TelemetryCalls);
        Assert.Equal(3, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void PciCapabilityApiAndHeaderFailuresDiscardDirtyOutputs(int mode)
    {
        var sdk = new Sdk { PciPropertiesMode = mode };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciCapabilityRequest(), CancellationToken.None));
        Assert.Equal(1, sdk.PciPropertiesCalls); Assert.Equal(0, sdk.PciCalls); Assert.Equal(0, sdk.TelemetryCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(2, null, 16d)] [InlineData(3, 5d, null)] [InlineData(8, 0d, 0d)]
    [InlineData(9, 2147483647d, 2147483647d)] [InlineData(10, null, null)] [InlineData(11, null, null)]
    public void PciMaximumGenerationAndWidthHaveIndependentUnknownAndExactZeroSemantics(int mode, double? generation, double? width)
    {
        var sdk = new Sdk { PciPropertiesMode = mode };
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), PciCapabilityRequest(), CancellationToken.None));
        Assert.Equal(generation, row.Values[PciCapabilityCodes[0]]); Assert.Equal(width, row.Values[PciCapabilityCodes[1]]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ThreeQueryClassesPreserveTwoSuccessfulClassesWhenOneFails(int failure)
    {
        var sdk = new Sdk { Mode = failure == 0 ? 1 : 0, PciMode = failure == 1 ? 1 : 0, PciPropertiesMode = failure == 2 ? 1 : 0 };
        var request = PciCapabilityRequest(); request[9] = ClockCodes.Concat(PciCodes).Concat(PciCapabilityCodes).ToHashSet();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(4, row.Values.Count); Assert.Equal(1, sdk.TelemetryCalls); Assert.Equal(1, sdk.PciCalls); Assert.Equal(1, sdk.PciPropertiesCalls);
        if (failure != 0) { Assert.Equal(1222.5, row.Values[ClockCodes[0]]); Assert.Equal(900.75, row.Values[ClockCodes[1]]); }
        if (failure != 1) { Assert.Equal(4, row.Values[PciCodes[0]]); Assert.Equal(8, row.Values[PciCodes[1]]); }
        if (failure != 2) { Assert.Equal(5, row.Values[PciCapabilityCodes[0]]); Assert.Equal(16, row.Values[PciCapabilityCodes[1]]); }
        Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void MissingCapabilityEntryKeepsCurrentStateAndTelemetryAvailable()
    {
        var sdk = new Sdk { MissingPciPropertiesEntry = true };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciCapabilityRequest(), CancellationToken.None));
        var request = PciCapabilityRequest(); request[9] = ClockCodes.Concat(PciCodes).Concat(PciCapabilityCodes).ToHashSet();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(4, row.Values.Count); Assert.Equal(4, row.Values[PciCodes[0]]); Assert.Equal(1222.5, row.Values[ClockCodes[0]]);
        Assert.Equal(0, sdk.PciPropertiesCalls); Assert.Equal(sdk.Opens, sdk.Closes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CancellationBeforeCapabilitiesOrAfterCapabilitiesStopsFutureQueries(bool afterState)
    {
        var sdk = new Sdk(); using var stop = new CancellationTokenSource();
        var request = PciCapabilityRequest();
        if (afterState) { sdk.AfterPci = stop.Cancel; request[9] = PciCodes.Concat(PciCapabilityCodes).ToHashSet(); }
        else sdk.AfterPciProperties = stop.Cancel;
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(sdk.Open).Read(Inventory(), request, stop.Token));
        Assert.Equal(afterState ? 0 : 1, sdk.PciPropertiesCalls); Assert.Equal(afterState ? 1 : 0, sdk.PciCalls);
        Assert.Equal(2, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void PciCapabilityChangedIdentityIsDiscardedAndUnrequestedClassesDoNotRun()
    {
        var sdk = new Sdk { IdentityMode = "changed-luid" };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciCapabilityRequest(), CancellationToken.None));
        Assert.Equal(1, sdk.PciPropertiesCalls); Assert.Equal(0, sdk.PciCalls); Assert.Equal(0, sdk.TelemetryCalls); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public async Task MaximumCapabilitiesReachOriginalWorkerFreshHttpAndPush()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture(); using var owner = CreateClockOwner(slots, secondHasDedicatedMemory: true);
        await owner.StartAsync(CancellationToken.None); var dashboard = PublishClockCatalog(owner);
        var bound = dashboard.Current!.FieldBindings.Values.Where(binding => PciCapabilityCodes.Contains(binding.Field.Code)).ToArray();
        Assert.Equal(4, bound.Length); Assert.All(bound, binding => { Assert.Equal("intel.igcl", binding.SourceId); Assert.Equal(MetricVendor.Intel, binding.Field.Vendor); });
        var gpuIdentity = RecordedGpuIdentities().Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey;
        var device = bound.Single(binding => binding.Field.Code == PciCapabilityCodes[0] && binding.DeviceIdentity == gpuIdentity).DeviceIdentity;
        var sdk = new Sdk(); var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id => id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No system memory demand"), () => throw new InvalidOperationException("No system memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, new WindowsGpuAdapterOrderMonitoringZone(Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelGclZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, PciCapabilityNames);
        Dictionary<string, DeviceMetricFieldValue> Fields(HardwareMetricSnapshot snapshot)
            => Assert.Single(MetricDeviceFields.Read(snapshot, device, PciCapabilityNames.ToHashSet())).Fields.ToDictionary(field => field.Name);
        await using var app = await StartIntelHttpAsync(sampler, dashboard);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + string.Join(',', PciCapabilityNames);
        Assert.All(Fields(sampler.ReadLatest(request) ?? dashboard.Current!).Values, field => Assert.Null(field.NumericValue));
        Assert.Equal(0, sdk.Opens); await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("igcl-pci-capabilities", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciCapabilityNames[0]].NumericValue != 5);
                Assert.Equal(16, Fields(stream.Current)[PciCapabilityNames[1]].NumericValue);
                Volatile.Write(ref sdk.PciPropertiesMode, 1);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciCapabilityNames[0]].NumericValue is not null);
                Assert.Null(Fields(stream.Current)[PciCapabilityNames[1]].NumericValue);
                Volatile.Write(ref sdk.PciPropertiesMode, 8);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciCapabilityNames[0]].NumericValue != 0);
                using (var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token))) CheckPciCapabilityWire(wire.RootElement[0].GetProperty("fields"));
                using var cancelPush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "igcl-pci-capabilities-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, cancelPush.Token); response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancelPush.Token));
                using var json = JsonDocument.Parse((await reader.ReadLineAsync(cancelPush.Token))!); CheckPciCapabilityWire(json.RootElement.GetProperty("value")[0].GetProperty("fields"));
                await cancelPush.CancelAsync();
            }
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token); gate.Release(); var calls = Volatile.Read(ref sdk.PciPropertiesCalls);
            using (var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token))) CheckPciCapabilityWire(fresh.RootElement[0].GetProperty("fields"));
            Assert.True(Volatile.Read(ref sdk.PciPropertiesCalls) > calls);
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            await gate.WaitAsync(timeout.Token); gate.Release();
            var afterFetch = Volatile.Read(ref sdk.PciPropertiesCalls);
            await Task.Delay(200, timeout.Token); Assert.Equal(afterFetch, Volatile.Read(ref sdk.PciPropertiesCalls));
            Assert.Equal(0, sdk.PciCalls); Assert.Equal(0, sdk.TelemetryCalls);
            lock (sdk.PciPropertiesDevices) Assert.All(sdk.PciPropertiesDevices, handle => Assert.Equal(new IntPtr(1), handle));
            await sampler.StopAsync(CancellationToken.None); Assert.Equal(sdk.Opens, sdk.Closes);
            Assert.All(Fields(sampler.ReadLatest(request)!).Values, field => Assert.Equal(0, field.NumericValue));
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static void CheckPciCapabilityWire(JsonElement fields)
    {
        Assert.Equal(2, fields.GetArrayLength()); var byName = fields.EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        Assert.Equal("generation", byName[PciCapabilityNames[0]].GetProperty("unit").GetString()); Assert.Equal("lanes", byName[PciCapabilityNames[1]].GetProperty("unit").GetString());
        Assert.All(byName.Values, field =>
        {
            Assert.Equal("intel.igcl", field.GetProperty("sourceId").GetString()); Assert.Equal("float64", field.GetProperty("valueKind").GetString());
            Assert.Equal(0, field.GetProperty("numericValue").GetDouble()); Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
        });
    }
}
