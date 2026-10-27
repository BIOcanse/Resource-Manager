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
    private static void AssertPublishedInformationFieldsMatchResponse(JsonElement state, JsonElement fields)
        => Assert.Equal(fields.GetRawText(), state.GetProperty("informationFields").GetRawText());

    [Fact]
    public async Task SingleFetchHttpRetainsDeclarationsClearsFailedValuesAndIncludesCurrentInformationAlongsideSnapshot()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with
        { MemoryModules = [DeviceInformationTestData.Module("dimm")], Batteries = [new("battery", "厂商", "声明序号", "X123")],
            StorageInformation = AtaExtendedCapacityInformationCollectorTests.Declarations(),
            NvidiaGpuInformation = NvidiaGpuDeclarationCollectorTests.Declarations(),
            AmdGpuInformation = AmdGpuDeclarationInformationCollectorTests.Declarations(),
            GpuInformation = WindowsGpuInformationCollectorTests.Declarations(), HidInformation = HidInformationCollectorTests.Declarations(),
            NetworkInterfaceInformation = NetworkAddressInformationCollectorTests.Declarations() };
        var reader = new Reader(snapshot, new InvalidOperationException("read failed"));
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var empty = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(0, empty.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(0, reader.ReadCount);
        var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields");
        var current = fixture.Provider.ReadState().InformationFields;
        Assert.Equal("厂商", current.Devices.Single(d => d.DeviceIdentity == "battery")
            .Fields.Single(f => f.Definition.Name == "battery_all_manufacturer_declared").Value!.Value.GetString());
        using var first = JsonDocument.Parse(sampledHttpFields1);
        Assert.Equal("18446744073709551615", Field(first.RootElement, "dimm", "memory_all_dimm_capacity").GetProperty("value").GetString());
        Assert.Equal("厂商", Field(first.RootElement, "battery", "battery_all_manufacturer_declared").GetProperty("value").GetString());
        Assert.Equal("X123", Field(first.RootElement, "battery", "battery_all_chemistry_declared").GetProperty("value").GetString());
        Assert.Equal(AtaExtendedCapacityInformationCollectorTests.MaximumCapacity,
            Field(first.RootElement, "ata", "storage_all_capacity_declared").GetProperty("value").GetString());
        Assert.Equal((UInt128.MaxValue - 17).ToString(), Field(first.RootElement, "nvme", "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes").GetProperty("value").GetString());
        Assert.Equal(StorageIdentifyInformationCollectorTests.NvmeRaw(), Field(first.RootElement, "nvme", "storage_all_nvme_identify_controller_raw").GetProperty("value").GetBytesFromBase64());
        Assert.Equal("95.00.01.00.02", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_vbios_version").GetProperty("value").GetString());
        Assert.Equal("8.10", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").GetString());
        Assert.Equal("020.003",Field(first.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_version").GetProperty("value").GetString());
        Assert.Equal("GDDR6",Field(first.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vram_type").GetProperty("value").GetString());
        Assert.Equal("113-TEST",Field(first.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_part_number").GetProperty("value").GetString());
        Assert.Equal("04/31/2024",Field(first.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_date_raw").GetProperty("value").GetString());
        Assert.Equal("FEDCBA9876543210", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_adapter_luid").GetProperty("value").GetString());
        Assert.Equal("32.0.15.8100", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_driver_version").GetProperty("value").GetString());
        Assert.Equal("10DE", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_pci_vendor_id").GetProperty("value").GetString());
        Assert.Equal("2684", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_pci_device_id").GetProperty("value").GetString());
        Assert.Equal("显卡 Fixture", Field(first.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_name").GetProperty("value").GetString());
        Assert.Equal((ushort)65,Field(first.RootElement,HidInformationCollectorTests.IdentityA,"hid_all_report_length_declared").GetProperty("value").GetProperty("inputBytes").GetUInt16());
        Assert.Equal(0xFFFFFFFEU,Field(first.RootElement,HidInformationCollectorTests.IdentityA,"hid_all_value_capability").GetProperty("value")[0].GetProperty("unitsExponentRaw").GetUInt32());
        Assert.Equal("FEDCBA9876543210", Field(first.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_interface_luid_reported").GetProperty("value").GetString());
        Assert.Equal("198.51.100.7", Field(first.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_unicast_address_reported").GetProperty("value")[0].GetProperty("address").GetString());
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        Assert.Equal(1, reader.ReadCount);
        var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields");
        using var failed = JsonDocument.Parse(sampledHttpFields2);
        Assert.Equal(JsonValueKind.Null, Field(failed.RootElement, "dimm", "memory_all_dimm_capacity").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(failed.RootElement, "battery", "battery_all_manufacturer_declared").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(failed.RootElement, "ata", "storage_all_capacity_declared").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(failed.RootElement, "nvme", "storage_all_nvme_identify_controller_raw").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(failed.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_vbios_version").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(failed.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").ValueKind);
        Assert.All(fixture.Provider.ReadState().InformationFields.Devices.Single(d => d.DeviceIdentity == NvidiaGpuDeclarationCollectorTests.IdentityA).Fields,
            static field => Assert.Null(field.Value));
        Assert.All(fixture.Provider.ReadState().InformationFields.Devices.Single(d => d.DeviceIdentity == HidInformationCollectorTests.IdentityA).Fields,
            static field => Assert.Null(field.Value));
        Assert.All(fixture.Provider.ReadState().InformationFields.Devices.Single(d => d.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA).Fields,
            static field => Assert.Null(field.Value));
        using var legacy = JsonDocument.Parse(ReadPublishedState(fixture.Provider, app));
        AssertPublishedInformationFieldsMatchResponse(legacy.RootElement, failed.RootElement);
        Assert.Equal("Machine Model", legacy.RootElement.GetProperty("snapshot").GetProperty("system").GetProperty("model").GetString());
        Assert.Equal(2, reader.ReadCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedPushUsesRealOwnerAndWorkerRecoversThenStopsWithoutDemand()
    {
        var first = DeviceInformationTestData.Snapshot() with { MemoryModules = [DeviceInformationTestData.Module("dimm")] };
        var third = first with { CapturedAt = first.CapturedAt.AddSeconds(2) };
        first = first with { Batteries = [new("battery", "厂商", "声明序号", "X123")] };
        third = third with { Batteries = first.Batteries };
        first = first with { StorageInformation = AtaExtendedCapacityInformationCollectorTests.Declarations(),
            NvidiaGpuInformation = NvidiaGpuDeclarationCollectorTests.Declarations(), GpuInformation = WindowsGpuInformationCollectorTests.Declarations(),
            HidInformation = HidInformationCollectorTests.Declarations(), NetworkInterfaceInformation=NetworkAddressInformationCollectorTests.Declarations(),
            NetworkEndpointInformation=new WindowsNetworkEndpointInformationReader(new NetworkEndpointInformationCollectorTests.Api()).Read() };
        third = third with { StorageInformation = first.StorageInformation,NvidiaGpuInformation=first.NvidiaGpuInformation, GpuInformation=first.GpuInformation,
            HidInformation=first.HidInformation,NetworkInterfaceInformation=first.NetworkInterfaceInformation,
            NetworkEndpointInformation=first.NetworkEndpointInformation };
        var reader = new Reader(first, new InvalidOperationException("source unavailable"), third);
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "facts", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var success = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        using var failure = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        using var recovery = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var successful = success.RootElement.GetProperty("value");
        Assert.Equal("18446744073709551615", Field(successful, "dimm", "memory_all_dimm_capacity").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, Field(failure.RootElement.GetProperty("value"), "dimm", "memory_all_dimm_capacity").GetProperty("value").ValueKind);
        Assert.Equal("18446744073709551615", Field(recovery.RootElement.GetProperty("value"), "dimm", "memory_all_dimm_capacity").GetProperty("value").GetString());
        Assert.Equal("厂商", Field(successful, "battery", "battery_all_manufacturer_declared").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, Field(failure.RootElement.GetProperty("value"), "battery", "battery_all_manufacturer_declared").GetProperty("value").ValueKind);
        Assert.Equal("X123", Field(recovery.RootElement.GetProperty("value"), "battery", "battery_all_chemistry_declared").GetProperty("value").GetString());
        Assert.Equal("8.10",Field(successful,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null,Field(failure.RootElement.GetProperty("value"),NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").ValueKind);
        Assert.Equal("8.10",Field(recovery.RootElement.GetProperty("value"),NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").GetString());
        foreach (var name in new[] { "gpu_all_adapter_luid", "gpu_all_name", "gpu_all_pci_vendor_id", "gpu_all_pci_device_id", "gpu_all_driver_version" })
        {
            var firstValue = Field(successful, NvidiaGpuDeclarationCollectorTests.IdentityA, name).GetProperty("value").GetString();
            Assert.NotNull(firstValue);
            Assert.Equal(JsonValueKind.Null, Field(failure.RootElement.GetProperty("value"), NvidiaGpuDeclarationCollectorTests.IdentityA, name).GetProperty("value").ValueKind);
            Assert.Equal(firstValue, Field(recovery.RootElement.GetProperty("value"), NvidiaGpuDeclarationCollectorTests.IdentityA, name).GetProperty("value").GetString());
        }
        foreach (var name in new[] { "hid_all_pnp_instance_id_declared", "hid_all_usage_declared", "hid_all_report_length_declared", "hid_all_buttons_capability", "hid_all_value_capability" })
        {
            var firstValue=Field(successful,HidInformationCollectorTests.IdentityA,name).GetProperty("value").GetRawText();
            Assert.NotEqual("null",firstValue);
            Assert.Equal(JsonValueKind.Null,Field(failure.RootElement.GetProperty("value"),HidInformationCollectorTests.IdentityA,name).GetProperty("value").ValueKind);
            Assert.Equal(firstValue,Field(recovery.RootElement.GetProperty("value"),HidInformationCollectorTests.IdentityA,name).GetProperty("value").GetRawText());
        }
        Assert.Equal(AtaExtendedCapacityInformationCollectorTests.Raw(), Field(successful, "ata", "storage_all_ata_identify_raw").GetProperty("value").GetBytesFromBase64());
        foreach (var name in new[] { "network_all_interface_guid_reported", "network_all_interface_luid_reported", "network_all_interface_index_reported", "network_all_unicast_address_reported", "network_all_route_reported" })
        {
            var firstValue=Field(successful,NetworkAddressInformationCollectorTests.IdentityA,name).GetProperty("value").GetRawText();
            Assert.NotEqual("null",firstValue);
            Assert.Equal(JsonValueKind.Null,Field(failure.RootElement.GetProperty("value"),NetworkAddressInformationCollectorTests.IdentityA,name).GetProperty("value").ValueKind);
            Assert.Equal(firstValue,Field(recovery.RootElement.GetProperty("value"),NetworkAddressInformationCollectorTests.IdentityA,name).GetProperty("value").GetRawText());
        }
        foreach (var frame in new[] { successful, failure.RootElement.GetProperty("value"), recovery.RootElement.GetProperty("value") })
            Assert.DoesNotContain(frame.GetProperty("devices").EnumerateArray().SelectMany(static device => device.GetProperty("fields").EnumerateArray()),
                static field => field.GetProperty("definition").GetProperty("name").GetString() is
                    "network_all_tcp_ipv4_endpoint_reported" or "network_all_tcp_ipv6_endpoint_reported"
                    or "network_all_udp_ipv4_endpoint_reported" or "network_all_udp_ipv6_endpoint_reported");
        Assert.Equal(AtaExtendedCapacityInformationCollectorTests.MaximumCapacity,
            Field(successful, "ata", "storage_all_capacity_declared").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, Field(failure.RootElement.GetProperty("value"), "ata", "storage_all_capacity_declared").GetProperty("value").ValueKind);
        Assert.Equal(AtaExtendedCapacityInformationCollectorTests.MaximumCapacity,
            Field(recovery.RootElement.GetProperty("value"), "ata", "storage_all_capacity_declared").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, Field(failure.RootElement.GetProperty("value"), "ata", "storage_all_ata_identify_raw").GetProperty("value").ValueKind);
        Assert.Equal((UInt128.MaxValue - 17).ToString(), Field(recovery.RootElement.GetProperty("value"), "nvme", "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes").GetProperty("value").GetString());
        Assert.Equal(third.CapturedAt, recovery.RootElement.GetProperty("value").GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(1, fixture.Provider.ReadState().ContentGeneration);
        await timeout.CancelAsync();
        response.Dispose();
        await fixture.WaitForNoDemandAndIdleAsync();
        var count = reader.ReadCount;
        var current = fixture.Provider.ReadState().InformationFields;
        await Task.Delay(1300);
        Assert.Equal(count, reader.ReadCount);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    [Fact]
    public async Task OptionalGpuEnumerationFailureKeepsDeclarationsWhileSuccessfulEmptyRemovesThem()
    {
        var healthy=DeviceInformationTestData.Snapshot() with { NvidiaGpuInformation=NvidiaGpuDeclarationCollectorTests.Declarations() };
        var reader=new Reader(healthy,healthy with { NvidiaGpuInformation=null },healthy,healthy with { NvidiaGpuInformation=[] });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        for (var phase=0;phase<4;phase++)
        {
            var sampledHttpFields3 = await client.GetStringAsync("/api/device-topology/fields");
            using var result=JsonDocument.Parse(sampledHttpFields3);
            if (phase==3)
                Assert.DoesNotContain(result.RootElement.GetProperty("devices").EnumerateArray(),static d=>d.GetProperty("deviceIdentity").GetString()==NvidiaGpuDeclarationCollectorTests.IdentityA);
            else
            {
                var field=Field(result.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_vbios_version");
                Assert.Equal(phase==1?null:"95.00.01.00.02",field.GetProperty("value").GetString());
                Assert.Equal("nvidia.nvml",field.GetProperty("definition").GetProperty("sourceId").GetString());
            }
            Assert.Equal(phase+1,reader.ReadCount);
        }
    }

    [Fact]
    public async Task PersistedGpuInfoExposesNullValuesUntilFirstRealCapture()
    {
        var snapshot=DeviceInformationTestData.Snapshot() with { NvidiaGpuInformation=NvidiaGpuDeclarationCollectorTests.Declarations(),
            AmdGpuInformation=AmdGpuDeclarationInformationCollectorTests.Declarations(),
            GpuInformation=WindowsGpuInformationCollectorTests.Declarations(),HidInformation=HidInformationCollectorTests.Declarations(),
            NetworkInterfaceInformation=NetworkAddressInformationCollectorTests.Declarations() };
        var persisted=new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion,"test",7,
            new DeviceTopologySemanticComparer().ComputeHash(snapshot),snapshot);
        var reader=new Reader(snapshot);
        using var fixture=new Fixture(reader,persisted);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        using var cached=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_version").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_driver_version").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_adapter_luid").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,HidInformationCollectorTests.IdentityA,"hid_all_usage_declared").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_unicast_address_reported").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null,Field(cached.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_route_reported").GetProperty("value").ValueKind);
        Assert.Equal(0,reader.ReadCount);
        var sampledHttpFields4 = await client.GetStringAsync("/api/device-topology/fields");
        using var live=JsonDocument.Parse(sampledHttpFields4);
        Assert.Equal("8.10",Field(live.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_nvidia_cuda_compute_capability").GetProperty("value").GetString());
        Assert.Equal("32.0.15.8100",Field(live.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_driver_version").GetProperty("value").GetString());
        Assert.Equal((ushort)4,Field(live.RootElement,HidInformationCollectorTests.IdentityA,"hid_all_usage_declared").GetProperty("value").GetProperty("usage").GetUInt16());
        Assert.Equal(2,Field(live.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_unicast_address_reported").GetProperty("value").GetArrayLength());
        Assert.Equal(3,Field(live.RootElement,NetworkAddressInformationCollectorTests.IdentityA,"network_all_route_reported").GetProperty("value").GetArrayLength());
        Assert.Equal(1,reader.ReadCount);
    }

    [Fact]
    public async Task WindowsGpuInventoryFailureKeepsEmptyDeclarationsThenRecoversAndRemovesOnSuccessfulEmpty()
    {
        var healthy=DeviceInformationTestData.Snapshot() with { GpuInformation=WindowsGpuInformationCollectorTests.Declarations() };
        var reader=new Reader(healthy,healthy with { GpuInformation=null },healthy,healthy with { GpuInformation=[] });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        for (var phase=0;phase<4;phase++)
        {
            var sampledHttpFields5 = await client.GetStringAsync("/api/device-topology/fields");
            using var result=JsonDocument.Parse(sampledHttpFields5);
            if (phase==3)
                Assert.DoesNotContain(result.RootElement.GetProperty("devices").EnumerateArray(),static d=>d.GetProperty("deviceIdentity").GetString()==NvidiaGpuDeclarationCollectorTests.IdentityA);
            else
            {
                var fields=fixture.Provider.ReadState().InformationFields.Devices.Single(d=>d.DeviceIdentity==NvidiaGpuDeclarationCollectorTests.IdentityA).Fields;
                Assert.Equal(5,fields.Count);
                if (phase==1) Assert.All(fields,static field=>Assert.Null(field.Value));
                else Assert.All(fields,static field=>Assert.NotNull(field.Value));
                var field=Field(result.RootElement,NvidiaGpuDeclarationCollectorTests.IdentityA,"gpu_all_driver_version");
                Assert.Equal(phase==1?null:"32.0.15.8100",field.GetProperty("value").GetString());
                Assert.Equal("windows.gpu-adapter-order",field.GetProperty("definition").GetProperty("sourceId").GetString());
            }
            Assert.Equal(phase+1,reader.ReadCount);
        }
    }

    [Fact]
    public async Task HidEnumerationFailureKeepsEmptyDeclarationsRecoversAndSuccessfulEmptyRemovesCollections()
    {
        var healthy=DeviceInformationTestData.Snapshot() with { HidInformation=HidInformationCollectorTests.Declarations() };
        var reader=new Reader(healthy,healthy with { HidInformation=null },healthy,healthy with { HidInformation=[] });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        for (var phase=0;phase<4;phase++)
        {
            var sampledHttpFields6 = await client.GetStringAsync("/api/device-topology/fields");
            using var result=JsonDocument.Parse(sampledHttpFields6);
            if (phase==3) Assert.DoesNotContain(result.RootElement.GetProperty("devices").EnumerateArray(),static d=>d.GetProperty("deviceIdentity").GetString()==HidInformationCollectorTests.IdentityA);
            else
            {
                var fields=fixture.Provider.ReadState().InformationFields.Devices.Single(d=>d.DeviceIdentity==HidInformationCollectorTests.IdentityA).Fields;
                Assert.Equal(5,fields.Count);
                if (phase==1) Assert.All(fields,static field=>Assert.Null(field.Value)); else Assert.All(fields,static field=>Assert.NotNull(field.Value));
                Assert.Equal("windows.hid.capabilities",Field(result.RootElement,HidInformationCollectorTests.IdentityA,"hid_all_usage_declared").GetProperty("definition").GetProperty("sourceId").GetString());
            }
            Assert.Equal(phase+1,reader.ReadCount);
        }
    }

    [Fact]
    public async Task RealWorkerRunsTheHidCollectorOnlyForSubscribedSnapshotsAndCachedInspectionDoesNotQueryIt()
    {
        var api=new HidInformationCollectorTests.RepeatedApi();
        var collector=new ResourceManager.ReadWrite.Windows.Devices.Hid.WindowsHidInformationReader(api);
        var reader=new Reader(() => DeviceInformationTestData.Snapshot() with { HidInformation=collector.Read() });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        using var initial=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(0,api.Enumerations); Assert.Equal(0,api.Opens);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream") {
            Content=JsonContent.Create(new { version=1,subscriptions=new[] {
                new { id="hid-worker",path="/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message=JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        Assert.Equal((ushort)65,Field(message.RootElement.GetProperty("value"),HidInformationCollectorTests.IdentityA,"hid_all_report_length_declared").GetProperty("value").GetProperty("inputBytes").GetUInt16());
        Assert.True(api.Opens>0); Assert.Equal(api.Enumerations,api.Opens);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads=api.Enumerations; var opens=api.Opens;
        var current=fixture.Provider.ReadState().InformationFields;
        using var cached=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(reads,api.Enumerations); Assert.Equal(opens,api.Opens);
        await Task.Delay(1300);
        Assert.Equal(reads,api.Enumerations); Assert.Equal(opens,api.Opens); Assert.Same(current,fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    [Fact]
    public async Task NetworkInterfaceDirectoryFailureRetainsNullDeclarationsThenRecoversAndRemovesOnSuccessEmpty()
    {
        var healthy=DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation=NetworkAddressInformationCollectorTests.Declarations() };
        var reader=new Reader(healthy,healthy with { NetworkInterfaceInformation=null },healthy,healthy with { NetworkInterfaceInformation=[] });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        for (var phase=0;phase<4;phase++)
        {
            var sampledHttpFields7 = await client.GetStringAsync("/api/device-topology/fields");
            using var result=JsonDocument.Parse(sampledHttpFields7);
            if (phase==3)
                Assert.DoesNotContain(result.RootElement.GetProperty("devices").EnumerateArray(),static d=>d.GetProperty("deviceIdentity").GetString()==NetworkAddressInformationCollectorTests.IdentityA);
            else
            {
                var fields=fixture.Provider.ReadState().InformationFields.Devices.Single(d=>d.DeviceIdentity==NetworkAddressInformationCollectorTests.IdentityA).Fields;
                Assert.Equal(5,fields.Count);
                if (phase==1) Assert.All(fields,static f=>Assert.Null(f.Value)); else Assert.All(fields,static f=>Assert.NotNull(f.Value));
            }
        }
    }

    [Fact]
    public async Task RealWorkerRunsNetworkTablesOnlyWithDemandAndCachedInspectionRetainsCurrent()
    {
        var api=new NetworkAddressInformationCollectorTests.Api();
        var collector=new WindowsNetworkInterfaceInformationReader(api);
        var reader=new Reader(() => DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation=collector.Read() });
        using var fixture=new Fixture(reader);
        await using var app=await StartAsync(fixture.Provider);
        using var client=Client(app);
        using var initial=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(0,api.InterfaceReads); Assert.Equal(0,api.UnicastReads); Assert.Equal(0,api.RouteReads);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream") {
            Content=JsonContent.Create(new { version=1,subscriptions=new[] {
                new { id="network-worker",path="/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message=JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        Assert.Equal("fe80::1234",Field(message.RootElement.GetProperty("value"),NetworkAddressInformationCollectorTests.IdentityA,"network_all_unicast_address_reported").GetProperty("value")[1].GetProperty("address").GetString());
        Assert.Equal("198.51.100.1",Field(message.RootElement.GetProperty("value"),NetworkAddressInformationCollectorTests.IdentityA,"network_all_route_reported").GetProperty("value")[0].GetProperty("nextHopAddress").GetString());
        Assert.True(api.InterfaceReads>0); Assert.Equal(api.InterfaceReads,api.UnicastReads); Assert.Equal(api.InterfaceReads,api.RouteReads);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads=api.InterfaceReads; var addresses=api.UnicastReads; var routes=api.RouteReads;
        var current=fixture.Provider.ReadState().InformationFields;
        using var cached=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(reads,api.InterfaceReads); Assert.Equal(addresses,api.UnicastReads); Assert.Equal(routes,api.RouteReads);
        await Task.Delay(1300);
        Assert.Equal(reads,api.InterfaceReads); Assert.Equal(addresses,api.UnicastReads); Assert.Equal(routes,api.RouteReads); Assert.Same(current,fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    [Fact]
    public async Task AmdInventoryFailureClearsKnownDeclarationsRecoversThenEmptyRemovesThem()
    {
        var healthy=DeviceInformationTestData.Snapshot() with { AmdGpuInformation=AmdGpuDeclarationInformationCollectorTests.Declarations() };
        var reader=new Reader(healthy,healthy with { AmdGpuInformation=null },healthy,healthy with { AmdGpuInformation=[] });
        using var fixture=new Fixture(reader); await using var app=await StartAsync(fixture.Provider); using var client=Client(app);
        for(var phase=0;phase<4;phase++)
        {
            var sampledHttpFields8 = await client.GetStringAsync("/api/device-topology/fields");
            using var message=JsonDocument.Parse(sampledHttpFields8);
            if(phase==3) Assert.DoesNotContain(message.RootElement.GetProperty("devices").EnumerateArray(),device=>device.GetProperty("deviceIdentity").GetString()==AmdGpuDeclarationInformationCollectorTests.IdentityA);
            else foreach(var name in new[] { "gpu_amd_vbios_version","gpu_amd_vram_type","gpu_amd_vbios_part_number","gpu_amd_vbios_date_raw" })
            {
                var field=Field(message.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,name);
                Assert.Equal(phase==1 ? JsonValueKind.Null : JsonValueKind.String,field.GetProperty("value").ValueKind);
                Assert.Equal("amd.adlx",field.GetProperty("definition").GetProperty("sourceId").GetString());
            }
        }
    }

    [Fact]
    public async Task RealWorkerRunsAmdDeclarationCollectorOnlyForDemandAndCachedInspectionAfterCancel()
    {
        var probe=AmdGpuDeclarationInformationCollectorTests.CreateCollectorProbe();
        var reader=new Reader(()=>DeviceInformationTestData.Snapshot() with { AmdGpuInformation=probe.Read() });
        using var fixture=new Fixture(reader); await using var app=await StartAsync(fixture.Provider); using var client=Client(app);
        using var initial=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app)); Assert.Equal(0,probe.Calls());
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream") {
            Content=JsonContent.Create(new { version=1,subscriptions=new[] { new { id="amd-worker",path="/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message=JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value=message.RootElement.GetProperty("value");
        Assert.Equal("020.003",Field(value,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_version").GetProperty("value").GetString());
        Assert.Equal("GDDR6",Field(value,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vram_type").GetProperty("value").GetString());
        Assert.Equal("04/31/2024",Field(value,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_date_raw").GetProperty("value").GetString());
        Assert.True(probe.Calls()>0);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var calls=probe.Calls(); var current=fixture.Provider.ReadState().InformationFields;
        using var cached=JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal("113-TEST",Field(cached.RootElement,AmdGpuDeclarationInformationCollectorTests.IdentityA,"gpu_amd_vbios_part_number").GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(calls,probe.Calls()); Assert.Same(current,fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    // A collector test needs the first observed value after the explicit
    // cached-current frame (empty/persisted is valid and must be delivered).
    private static async Task<string> ReadFirstObservedInformationAsync(StreamReader stream, CancellationToken token, string timestampProperty = "observedAt")
    {
        var first = (await stream.ReadLineAsync(token))!;
        using var frame = JsonDocument.Parse(first);
        if (frame.RootElement.GetProperty("value").GetProperty(timestampProperty).ValueKind != JsonValueKind.Null) return first;
        return (await stream.ReadLineAsync(token))!;
    }

}
