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
    private static readonly MetricFieldCode[] PciCodes = [MetricFieldCode.IgclPciCurrentGeneration, MetricFieldCode.IgclPciCurrentWidth];
    private static readonly string[] PciNames = ["gpu_intel_pcie_current_generation", "gpu_intel_pcie_current_width"];
    private static Dictionary<int, IReadOnlySet<MetricFieldCode>> PciRequest(int index = 9)
        => new() { [index] = PciCodes.ToHashSet() };

    [Fact]
    public void PciLayoutsAndSelectedReadMatchTheOfficialCurrentStateQuery()
    {
        Assert.Equal(24, Marshal.SizeOf<IntelGclPciSpeed>()); Assert.Equal(32, Marshal.SizeOf<IntelGclPciState>());
        Assert.Equal(8, Marshal.OffsetOf<IntelGclPciState>(nameof(IntelGclPciState.Speed)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<IntelGclPciSpeed>(nameof(IntelGclPciSpeed.Generation)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<IntelGclPciSpeed>(nameof(IntelGclPciSpeed.Width)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<IntelGclPciSpeed>(nameof(IntelGclPciSpeed.MaximumBandwidth)).ToInt32());
        var sdk = new Sdk(); var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), PciRequest(3), CancellationToken.None));
        Assert.Equal(3, row.Index); Assert.Equal(2, row.Values.Count); Assert.Empty(row.Flags); Assert.Empty(row.Counters);
        Assert.Equal(4, row.Values[PciCodes[0]]); Assert.Equal(8, row.Values[PciCodes[1]]);
        Assert.Equal(new IntPtr(2), Assert.Single(sdk.PciDevices)); Assert.Equal(0, sdk.TelemetryCalls);
        Assert.Equal(3, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void PciApiAndOuterOrNestedHeaderFailuresDiscardDirtyValues(int mode)
    {
        var sdk = new Sdk { PciMode = mode };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciRequest(), CancellationToken.None));
        Assert.Equal(1, sdk.PciCalls); Assert.Equal(0, sdk.TelemetryCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(2, null, 8d)] [InlineData(3, 4d, null)] [InlineData(8, 0d, 0d)]
    [InlineData(9, 2147483647d, 2147483647d)] [InlineData(10, null, null)] [InlineData(11, null, null)]
    public void UnknownPropertiesStayIndependentAndNonnegativeInt32IsExact(int mode, double? generation, double? width)
    {
        var sdk = new Sdk { PciMode = mode };
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), PciRequest(), CancellationToken.None));
        Assert.Equal(generation, row.Values[PciCodes[0]]); Assert.Equal(width, row.Values[PciCodes[1]]);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MixedDemandKeepsTheSuccessfulQueryWhenTheOtherQueryFails(bool failTelemetry)
    {
        var sdk = new Sdk { Mode = failTelemetry ? 1 : 0, PciMode = failTelemetry ? 0 : 1 };
        var request = PciRequest(); request[9] = PciCodes.Concat(ClockCodes).ToHashSet();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(2, row.Values.Count); Assert.Equal(1, sdk.TelemetryCalls); Assert.Equal(1, sdk.PciCalls);
        if (failTelemetry) { Assert.Equal(4, row.Values[PciCodes[0]]); Assert.Equal(8, row.Values[PciCodes[1]]); }
        else { Assert.Equal(1222.5, row.Values[ClockCodes[0]]); Assert.Equal(900.75, row.Values[ClockCodes[1]]); }
        Assert.Equal(sdk.Opens, sdk.Closes);
    }

    [Fact]
    public void MissingPciEntryDoesNotDisableOriginalTelemetry()
    {
        var sdk = new Sdk { MissingPciEntry = true };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciRequest(), CancellationToken.None));
        var request = PciRequest(); request[9] = PciCodes.Concat(ClockCodes).ToHashSet();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(2, row.Values.Count); Assert.Equal(1222.5, row.Values[ClockCodes[0]]);
        Assert.Equal(0, sdk.PciCalls); Assert.Equal(1, sdk.TelemetryCalls); Assert.Equal(sdk.Opens, sdk.Closes);
    }

    [Fact]
    public void PciDemandRespectsVendorAndPostReadIdentity()
    {
        var sdk = new Sdk();
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory() with { Adapters = [Adapter(9, LuidA, 0x1002)] }, PciRequest(), CancellationToken.None));
        Assert.Equal(0, sdk.Opens);
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), new Dictionary<int, IReadOnlySet<MetricFieldCode>>(), CancellationToken.None));
        Assert.Equal(0, sdk.Opens);
        sdk.IdentityMode = "changed-luid";
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), PciRequest(), CancellationToken.None));
        Assert.Equal(1, sdk.PciCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CancellationBetweenQueriesOrAfterPciStopsFutureNativeCalls(bool afterTelemetry)
    {
        var sdk = new Sdk(); using var stop = new CancellationTokenSource();
        if (afterTelemetry) sdk.AfterTelemetry = stop.Cancel; else sdk.AfterPci = stop.Cancel;
        var request = PciRequest(); if (afterTelemetry) request[9] = PciCodes.Concat(ClockCodes).ToHashSet();
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(sdk.Open).Read(Inventory(), request, stop.Token));
        Assert.Equal(afterTelemetry ? 0 : 1, sdk.PciCalls); Assert.Equal(afterTelemetry ? 1 : 0, sdk.TelemetryCalls);
        Assert.Equal(2, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public async Task OriginalWorkerPublishesCurrentPciStateThroughCacheHttpAndPush()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = CreateClockOwner(slots, secondHasDedicatedMemory: true);
        await owner.StartAsync(CancellationToken.None); var dashboard = PublishClockCatalog(owner);
        var bound = dashboard.Current!.FieldBindings.Values.Where(binding => PciCodes.Contains(binding.Field.Code)).ToArray();
        Assert.Equal(4, bound.Length); Assert.All(bound, binding => { Assert.Equal("intel.igcl", binding.SourceId); Assert.Equal(MetricVendor.Intel, binding.Field.Vendor); });
        var device = bound.Single(binding => binding.Field.Code == PciCodes[0] && binding.DeviceIdentity.Contains("gpu-a")).DeviceIdentity;
        var sdk = new Sdk(); var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id => id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No system memory demand"), () => throw new InvalidOperationException("No system memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, new WindowsGpuAdapterOrderMonitoringZone(Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelGclZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, PciNames);
        Dictionary<string, DeviceMetricFieldValue> Fields(HardwareMetricSnapshot snapshot)
            => Assert.Single(MetricDeviceFields.Read(snapshot, device, PciNames.ToHashSet())).Fields.ToDictionary(field => field.Name);
        await using var app = await StartIntelHttpAsync(sampler, dashboard);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + string.Join(',', PciNames);
        Assert.All(Fields(await sampler.GetSnapshotAsync(request, timeout.Token)).Values, field => Assert.Null(field.NumericValue));
        using (var cold = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token)))
            Assert.All(cold.RootElement[0].GetProperty("fields").EnumerateArray(), field => Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind));
        Assert.Equal(0, sdk.Opens); await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("igcl-pci", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciNames[0]].NumericValue != 4);
                Assert.Equal(8, Fields(stream.Current)[PciNames[1]].NumericValue);
                Volatile.Write(ref sdk.PciMode, 2);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciNames[0]].NumericValue is not null);
                Assert.Equal(8, Fields(stream.Current)[PciNames[1]].NumericValue);
                Volatile.Write(ref sdk.PciMode, 1);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciNames[1]].NumericValue is not null);
                Assert.Null(Fields(stream.Current)[PciNames[0]].NumericValue);
                Volatile.Write(ref sdk.PciMode, 8);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[PciNames[0]].NumericValue != 0);
                Assert.Equal(0, Fields(stream.Current)[PciNames[1]].NumericValue);
                using (var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token))) CheckPciWire(wire.RootElement[0].GetProperty("fields"));
                using var cancelPush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "igcl-pci-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, cancelPush.Token); response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancelPush.Token));
                using var json = JsonDocument.Parse((await reader.ReadLineAsync(cancelPush.Token))!); CheckPciWire(json.RootElement.GetProperty("value")[0].GetProperty("fields"));
                await cancelPush.CancelAsync();
            }
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
                .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token); gate.Release(); var calls = Volatile.Read(ref sdk.PciCalls);
            using (var cached = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token))) CheckPciWire(cached.RootElement[0].GetProperty("fields"));
            await Task.Delay(200, timeout.Token); Assert.Equal(calls, Volatile.Read(ref sdk.PciCalls));
            Assert.Equal(0, sdk.TelemetryCalls); lock (sdk.PciDevices) Assert.All(sdk.PciDevices, handle => Assert.Equal(new IntPtr(1), handle));
            await sampler.StopAsync(CancellationToken.None); Assert.Equal(sdk.Opens, sdk.Closes);
            Assert.All(Fields(await sampler.GetSnapshotAsync(request, timeout.Token)).Values, field => Assert.Equal(0, field.NumericValue));
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static void CheckPciWire(JsonElement fields)
    {
        Assert.Equal(2, fields.GetArrayLength()); var byName = fields.EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        Assert.Equal("generation", byName[PciNames[0]].GetProperty("unit").GetString()); Assert.Equal("lanes", byName[PciNames[1]].GetProperty("unit").GetString());
        Assert.All(byName.Values, field =>
        {
            Assert.Equal("intel.igcl", field.GetProperty("sourceId").GetString()); Assert.Equal("float64", field.GetProperty("valueKind").GetString());
            Assert.Equal(0, field.GetProperty("numericValue").GetDouble()); Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
        });
    }
}
