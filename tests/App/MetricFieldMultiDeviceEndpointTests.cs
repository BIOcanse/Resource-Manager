using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class MetricFieldMultiDeviceEndpointTests(ITestOutputHelper output)
{
    private const string Frequency = "cpu_intel_effective_frequency_mhz";
    private const string Counter = "cpu_intel_aperf_counter_raw";
    private const string Query = "?fields=" + Frequency + "," + Counter;

    [Fact]
    public async Task RepeatedDeviceAndFieldGroupsSelectPairsForGetAndSubscription()
    {
        var snapshot = CreateSnapshot();
        var source = new Source(snapshot);
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var query = "?device=opaque-instance-00&fields=" + Frequency
            + "&device=opaque-instance-02&fields=" + Counter;
        var expected = snapshot.FieldBindings.Values.Where(binding =>
            binding.DeviceIdentity == "opaque-instance-00" && binding.Field.Name == Frequency
            || binding.DeviceIdentity == "opaque-instance-02" && binding.Field.Name == Counter)
            .Select(binding => binding.RequestAddress).Order().ToArray();
        using var get = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query));
        CheckPairs(get.RootElement);
        Assert.Equal(expected, source.Request!.Ids.Order());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(Channel(query)) }, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var frame = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        CheckPairs(frame.RootElement.GetProperty("value"));
        Assert.Equal(expected, source.Request!.Ids.Order());
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();

        static void CheckPairs(JsonElement devices)
        {
            var rows = devices.EnumerateArray().ToDictionary(row => row.GetProperty("deviceIdentity").GetString()!);
            Assert.Equal(2, rows.Count);
            Assert.Equal(Frequency, Assert.Single(rows["opaque-instance-00"].GetProperty("fields").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(Counter, Assert.Single(rows["opaque-instance-02"].GetProperty("fields").EnumerateArray()).GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task CatalogAndFieldPushExposeUsableDeviceDescriptionsAndRecordActualWire()
    {
        const string gpuPnp = @"PCI\VEN_1002&DEV_1234\GpuMixed";
        const string storagePnp = @"SCSI\DISK&VEN_FIXTURE\DiskMixed";
        const string networkPnp = @"PCI\VEN_8086&DEV_1234\NetworkMixed";
        const string networkGuid = "26822419-2D18-4135-AB22-224B30376142";
        var storage = Port(storagePnp) with
        {
            Storage = new(@"\\.\PhysicalDrive3", "Hardware SSD", null, "DISK-SERIAL-A", null, null, null,
                null, null, null, null, [], new BackendMessage(0, 0, []))
        };
        var network = Port(networkPnp) with
        {
            Network = new(null, "unknown", null, null, null, null, null, null)
                { InterfaceGuid = networkGuid, InterfaceDescription = "Hardware adapter" }
        };
        var descriptors = new (string Id, string Field, MetricDeviceDescriptor Device, double? Number, string? Integer)[]
        {
            ("wire-cpu", Frequency, new("cpu_logical_processor", MetricDeviceAddress.LogicalProcessor(1, 2), null, 0), 3200, null),
            ("wire-numa", "memory_all_numa_node_capacity_mib", new("numa_node", MetricDeviceAddress.NumaNode(7), null, 0), null, "65536"),
            ("wire-gpu", "gpu_amd_npu_activity_percent", new("gpu_adapter", MetricDeviceAddress.GpuAdapter(0xA1, [gpuPnp.ToUpperInvariant()]), "Hardware GPU", 0), 37.5, null),
            ("wire-npu", "npu_all_engine_busy_percent", new("npu", MetricDeviceAddress.Npu(@"ACPI\NPU0"), "Hardware NPU", 0), 125.5, null),
            ("wire-storage", "storage_all_nvme_composite_temperature_celsius", new("storage", MetricDeviceAddress.StorageDevice(17, "DISK-UID-A", "DISK-SERIAL-A"), null, 0), 40, null),
            ("wire-fan", "fan_all_speed_rpm", new("fan", MetricDeviceAddress.SystemFan("fixture.wmi", "/sensor/fan/0"), null, 0), 1200, null),
            ("wire-network", "network_all_rx_errors_actual", new("network_interface", MetricDeviceAddress.NetworkInterface(Guid.Parse(networkGuid), ulong.MaxValue, 7), null, 0), null, "18446744073709551615")
        };
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot() with
        {
            FieldBindings = descriptors.ToDictionary(row => row.Id, row => new MetricDeviceFieldBinding(
                row.Id, MetricFieldRegistry.FindByName(row.Field)!, row.Id, "fixture") { Device = row.Device }),
            Items = descriptors.ToDictionary(row => row.Id, row => new MetricReadingValue(Id: row.Id, NumericValue: row.Number, Unit: MetricFieldRegistry.FindByName(row.Field)!.Unit, Percent: null)
                { IntegerValue = row.Integer is null ? null : MetricInteger.Parse(row.Integer), SourceId = "fixture" }),
            Datasets = descriptors.ToDictionary(row => row.Id, row => new HardwareMetricDatasetObservation(
                row.Id, SamplingObservationStatus.Current, 1, now.UtcTicks, 1, 1, 1, 1))
        };
        var source = new Source(snapshot);
        await using var app = await StartAsync(source, topology: new Topology([gpuPnp], [storage, network]));
        using var client = Client(app);
        var catalogText = await client.GetStringAsync("/api/metrics/fields/catalog");
        using var catalog = JsonDocument.Parse(catalogText);
        CheckDevices(catalog.RootElement);
        var catalogDevices = catalog.RootElement.EnumerateArray().ToDictionary(
            device => device.GetProperty("deviceIdentity").GetString()!, device => device.GetProperty("device").GetRawText());
        foreach (var device in catalog.RootElement.EnumerateArray())
            foreach (var field in device.GetProperty("fields").EnumerateArray())
            {
                var definition = MetricFieldRegistry.FindByName(field.GetProperty("name").GetString()!)!;
                Assert.Equal(definition.Unit, field.GetProperty("unit").GetString());
                Assert.Equal(definition.ValueKind, field.GetProperty("valueKind").GetString());
                Assert.False(field.TryGetProperty("meaning", out _));
            }
        output.WriteLine("METRIC_FIELDS_CATALOG_WIRE " + catalogText);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var catalogRequest = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[]
            { new { id = "directory", path = "/api/metrics/fields/catalog/subscribe" } } }) })
        using (var catalogResponse = await client.SendAsync(catalogRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            using var reader = new StreamReader(await catalogResponse.Content.ReadAsStreamAsync(timeout.Token));
            using var pushedCatalog = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var envelope = pushedCatalog.RootElement.GetProperty("value");
            Assert.Equal(app.Services.GetRequiredService<HardwareMetricCatalogState>().Revision,
                envelope.GetProperty("catalogRevision").GetString());
            Assert.NotEmpty(envelope.GetProperty("metrics").EnumerateArray());
            CheckDevices(envelope.GetProperty("fields"));
            foreach (var value in envelope.GetProperty("fields").EnumerateArray())
                Assert.Equal(catalogDevices[value.GetProperty("deviceIdentity").GetString()!], value.GetProperty("device").GetRawText());
        }
        var query = "?fields=" + string.Join(',', descriptors.Select(row => row.Field));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(Channel(query)) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var unavailable = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        CheckDevices(unavailable.RootElement.GetProperty("value"));
        var currentText = (await stream.ReadLineAsync(timeout.Token))!;
        using var current = JsonDocument.Parse(currentText);
        CheckDevices(current.RootElement.GetProperty("value"));
        var values = current.RootElement.GetProperty("value").EnumerateArray()
            .ToDictionary(device => device.GetProperty("deviceIdentity").GetString()!);
        foreach (var value in values)
            Assert.Equal(catalogDevices[value.Key], value.Value.GetProperty("device").GetRawText());
        Assert.Equal("18446744073709551615", values["wire-network"].GetProperty("fields")[0].GetProperty("integerValue").GetString());
        Assert.Equal(125.5, values["wire-npu"].GetProperty("fields")[0].GetProperty("numericValue").GetDouble());
        output.WriteLine("METRIC_FIELDS_SUBSCRIPTION_WIRE " + currentText);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();

        static void CheckDevices(JsonElement devices)
        {
            Assert.Equal(7, devices.GetArrayLength());
            var values = devices.EnumerateArray().ToDictionary(device => device.GetProperty("deviceIdentity").GetString()!,
                device => device.GetProperty("device"));
            foreach (var value in values.Values)
            {
                Assert.Equal(JsonValueKind.String, value.GetProperty("kind").ValueKind);
                Assert.Equal(JsonValueKind.Object, value.GetProperty("address").ValueKind);
                Assert.Equal(JsonValueKind.Number, value.GetProperty("ordinal").ValueKind);
                Assert.Equal(0, value.GetProperty("ordinal").GetInt32());
                Assert.True(value.TryGetProperty("displayName", out _));
            }
            Assert.Equal(gpuPnp, values["wire-gpu"].GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal(storagePnp, values["wire-storage"].GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal(networkPnp, values["wire-network"].GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal("Hardware SSD", values["wire-storage"].GetProperty("displayName").GetString());
            Assert.Equal("Hardware adapter", values["wire-network"].GetProperty("displayName").GetString());
            Assert.Equal(@"ACPI\NPU0", values["wire-npu"].GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal("/sensor/fan/0", values["wire-fan"].GetProperty("address").GetProperty("sensorIdentifier").GetString());
            Assert.Equal("FFFFFFFFFFFFFFFF", values["wire-network"].GetProperty("address").GetProperty("interfaceLuid").GetString());
        }
    }

    [Fact]
    public async Task PnpAddressesUseExactCachedPortSpellingAndKeepMissingPortsUnmatched()
    {
        const string gpuPort = @"PCI\VEN_1002&DEV_abcd\MiXeD";
        const string npuPort = @"ACPI\Npu_Mixed";
        var snapshot = CreateSnapshot();
        var bindings = new Dictionary<string, MetricDeviceFieldBinding>();
        foreach (var (device, field, kind, address) in new[]
        {
            ("opaque-gpu", "gpu_amd_npu_activity_percent", "gpu_adapter", MetricDeviceAddress.GpuAdapter(0xA1, [gpuPort.ToUpperInvariant()])),
            ("opaque-npu", "npu_all_engine_busy_percent", "npu", MetricDeviceAddress.Npu(npuPort.ToUpperInvariant())),
            ("missing-npu", "npu_all_engine_busy_percent", "npu", MetricDeviceAddress.Npu(@"ACPI\NOT_IN_PORTS"))
        })
            bindings[device] = new(device, MetricFieldRegistry.FindByName(field)!, device, "fixture")
                { Device = new(kind, address, null, bindings.Count) };
        var source = new Source(snapshot with { FieldBindings = bindings });
        var topology = new Topology([gpuPort, npuPort]);
        await using var app = await StartAsync(source, topology: topology);
        using var client = Client(app);
        const string query = "?fields=gpu_amd_npu_activity_percent,npu_all_engine_busy_percent";
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query));
        CheckPnp(cached.RootElement);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(Channel(query)) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var pushed = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        CheckPnp(pushed.RootElement.GetProperty("value"));
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();

        static void CheckPnp(JsonElement devices)
        {
            var values = devices.EnumerateArray().ToDictionary(device => device.GetProperty("deviceIdentity").GetString()!);
            Assert.Equal(gpuPort, values["opaque-gpu"].GetProperty("device").GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal(npuPort, values["opaque-npu"].GetProperty("device").GetProperty("address").GetProperty("pnpInstanceId").GetString());
            Assert.Equal(@"ACPI\NOT_IN_PORTS", values["missing-npu"].GetProperty("device").GetProperty("address").GetProperty("pnpInstanceId").GetString());
        }
    }

    [Fact]
    public async Task OneFieldSubscriptionCoversSeventyDevicesAndOnlyTheirBoundFields()
    {
        var source = new Source(CreateSnapshot());
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + Query));
        Check(cached.RootElement, current: true);
        Assert.Equal(105, source.Request!.Ids.Count);
        Assert.False(source.Request.IsAll);
        Assert.False(source.Request.IsEmpty);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(Channel(Query)) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var unavailable = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Check(unavailable.RootElement.GetProperty("value"), current: false);
        using var current = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Check(current.RootElement.GetProperty("value"), current: true);
        Assert.Equal(1, source.SubscriptionCount);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(105, source.Request!.Ids.Count);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task KnownUnboundFieldIsAnExplicitEmptySelectionForReadAndPush()
    {
        var source = new Source(CreateSnapshot());
        await using var app = await StartAsync(source);
        using var client = Client(app);
        const string query = "?fields=npu_all_engine_busy_percent";
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query));
        Assert.Equal(0, cached.RootElement.GetArrayLength());
        Assert.True(source.Request!.IsEmpty);
        Assert.False(source.Request.IsAll);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(Channel(query)) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var pushed = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(0, pushed.RootElement.GetProperty("value").GetArrayLength());
        Assert.True(source.Request!.IsEmpty);
        Assert.False(source.Request.IsAll);
        Assert.Equal(0, source.SubscriptionCount);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Theory]
    [InlineData("?fields=cpu_intel_effective_frequency_mhz,unknown_field")]
    [InlineData("?fields=")]
    [InlineData("?device=&fields=cpu_intel_effective_frequency_mhz")]
    [InlineData("?device=opaque-instance-01&fields=cpu_intel_aperf_counter_raw")]
    [InlineData("?device=unknown&fields=cpu_intel_effective_frequency_mhz")]
    [InlineData("?device=opaque-instance-00&device=opaque-instance-01&fields=cpu_intel_effective_frequency_mhz")]
    public async Task InvalidFieldsAndStrictSingleDeviceSelectionReturn400WithoutDemand(string query)
    {
        var source = new Source(CreateSnapshot());
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var read = await client.GetAsync("/api/metrics/fields/snapshot" + query);
        using var push = await client.PostAsJsonAsync("/api/subscriptions/stream", Channel(query));
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, push.StatusCode);
        Assert.Null(source.Request);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, source.SubscriptionCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task MissingDeviceDoesNotChangeUnreadyCatalog503OrSingleDeviceResults()
    {
        var source = new Source(CreateSnapshot());
        await using (var unready = await StartAsync(source, ready: false))
        {
            using var client = Client(unready);
            using var read = await client.GetAsync("/api/metrics/fields/snapshot" + Query);
            using var push = await client.PostAsJsonAsync("/api/subscriptions/stream", Channel(Query));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, push.StatusCode);
            Assert.Null(source.Request);
            await unready.StopAsync();
        }
        await using var ready = await StartAsync(source);
        using var singleClient = Client(ready);
        using var single = JsonDocument.Parse(await singleClient.GetStringAsync(
            "/api/metrics/fields/snapshot?device=opaque-instance-01&fields=" + Frequency));
        Assert.Equal("opaque-instance-01", Assert.Single(single.RootElement.EnumerateArray()).GetProperty("deviceIdentity").GetString());
        Assert.Equal(1, single.RootElement[0].GetProperty("device").GetProperty("ordinal").GetInt32());
        Assert.Single(source.Request!.Ids);
        using var counters = JsonDocument.Parse(await singleClient.GetStringAsync(
            "/api/metrics/fields/snapshot?fields=" + Counter));
        var selected = counters.RootElement.EnumerateArray().ToDictionary(device => device.GetProperty("deviceIdentity").GetString()!);
        Assert.Equal(35, selected.Count);
        for (var index = 0; index < 70; index += 2)
            Assert.Equal(index, selected[$"opaque-instance-{index:D2}"].GetProperty("device").GetProperty("ordinal").GetInt32());
        await ready.StopAsync();
    }

    [Theory]
    [InlineData(SamplingObservationStatus.Invalid)]
    [InlineData(SamplingObservationStatus.RetainedLastGood)]
    [InlineData(SamplingObservationStatus.Unavailable)]
    [InlineData(SamplingObservationStatus.NotRequested)]
    [InlineData(SamplingObservationStatus.Unsupported)]
    [InlineData(SamplingObservationStatus.Partial)]
    [InlineData(SamplingObservationStatus.Warming)]
    public async Task EveryNonCurrentStatusReturnsNullValuesEvenIfOldItemsRemain(SamplingObservationStatus status)
    {
        var snapshot = CreateSnapshot();
        var source = new Source(snapshot with { Datasets = snapshot.Datasets.ToDictionary(
            pair => pair.Key, pair => pair.Value with { Status = status }) });
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var result = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + Query));
        Check(result.RootElement, current: false);
        await app.StopAsync();
    }

    private static void Check(JsonElement devices, bool current)
    {
        Assert.Equal(70, devices.GetArrayLength());
        var byIdentity = devices.EnumerateArray().ToDictionary(d => d.GetProperty("deviceIdentity").GetString()!);
        for (var index = 0; index < 70; index++)
        {
            var device = byIdentity[$"opaque-instance-{index:D2}"];
            var descriptor = device.GetProperty("device");
            var address = descriptor.GetProperty("address");
            Assert.Equal("cpu_logical_processor", descriptor.GetProperty("kind").GetString());
            Assert.Equal("cpu_logical_processor", address.GetProperty("kind").GetString());
            Assert.Equal(index, descriptor.GetProperty("ordinal").GetInt32());
            Assert.Equal(JsonValueKind.Null, descriptor.GetProperty("displayName").ValueKind);
            Assert.Equal(index / 64, address.GetProperty("processorGroup").GetInt32());
            Assert.Equal(index % 64, address.GetProperty("groupRelativeIndex").GetInt32());
            Assert.False(address.TryGetProperty("pnpInstanceId", out _));
            var fields = device.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
            Assert.Equal(index % 2 == 0 ? 2 : 1, fields.Count);
            if (current)
            {
                Assert.Equal(3200 + index, fields[Frequency].GetProperty("numericValue").GetDouble());
                if (index % 2 == 0)
                {
                    Assert.Equal("unsigned64", fields[Counter].GetProperty("valueKind").GetString());
                    Assert.Equal("18446744073709551615", fields[Counter].GetProperty("integerValue").GetString());
                }
            }
            else
                foreach (var field in fields.Values)
                    foreach (var name in new[] { "numericValue", "integerValue", "bytesValue", "booleanValue", "sampleDurationMilliseconds", "observedAt" })
                        Assert.Equal(JsonValueKind.Null, field.GetProperty(name).ValueKind);
        }
    }

    private static HardwareMetricSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var bindings = new Dictionary<string, MetricDeviceFieldBinding>();
        var items = new Dictionary<string, MetricReadingValue>();
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>();
        for (var index = 0; index < 70; index++)
        {
            Add(index, Frequency);
            if (index % 2 == 0) Add(index, Counter);
        }
        const string unrelated = "unrelated/memory";
        bindings[unrelated] = new("unrelated", MetricFieldRegistry.FindByName("memory_all_physical_available_bytes")!, unrelated, "fixture")
            { Device = new("memory", null, null, 0) };
        return new(now, new("fixture", 0, false, CpuMetricObservationStatus.NotRequested, 0, 0, 0, 0, 0, "fixture",
            new(new("fixture", HardwareSensorProviderStates.NotRequested, null), null, null, null, null)),
            new(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false), new(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false), [],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []), items)
        {
            FieldBindings = bindings, Datasets = datasets,
            WorkspaceIdentity = 1, ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1
        };

        void Add(int index, string name)
        {
            var field = MetricFieldRegistry.FindByName(name)!;
            var device = $"opaque-instance-{index:D2}";
            var address = $"{device}/{name}";
            bindings[address] = new(device, field, address, "fixture")
                { Device = new("cpu_logical_processor", MetricDeviceAddress.LogicalProcessor((ushort)(index / 64), (byte)(index % 64)), null, index) };
            items[address] = new(Id: address, NumericValue: name == Frequency ? 3200 + index : null, Unit: field.Unit, Percent: null)
                { SourceId = "fixture", IntegerValue = name == Counter ? MetricInteger.Parse("18446744073709551615") : null };
            datasets[address] = new(address, SamplingObservationStatus.Current, 1, now.UtcTicks, 1, 1, 1, 1);
        }
    }

    private static object Channel(string query) => new
        { version = 1, subscriptions = new[] { new { id = "fields", path = "/api/metrics/subscribe" + query } } };

    private static async Task<WebApplication> StartAsync(Source source, bool ready = true, Topology? topology = null)
    {
        var catalog = new HardwareMetricCatalogState();
        if (ready) catalog.PublishCatalogProbe(source.Current);
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IMetricSampler>(source);
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(source);
        builder.Services.AddSingleton<IComponentManager, NoInstalledComponentManager>();
        if (topology is not null) builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(topology);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var name in new[] { "MapMetricFieldEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient()
    {
        BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(10)
    }, app.Services);

    private sealed class Source(HardwareMetricSnapshot snapshot) : IMetricSampler, IMetricSnapshotPushSource
    {
        internal HardwareMetricSnapshot Current { get; } = snapshot;
        internal MetricSampleRequest? Request { get; private set; }
        internal int ReadCount { get; private set; }
        internal int SubscriptionCount { get; private set; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        {
            Request = request;
            ReadCount++;
            return Task.FromResult(Current);
        }
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => throw new InvalidOperationException("Field reads must not trigger collection.");
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            Assert.False(request.IsEmpty, "An empty field selection must not start sampling demand.");
            Request = request;
            SubscriptionCount++;
            yield return Current with { Datasets = Current.Datasets.ToDictionary(
                pair => pair.Key, pair => pair.Value with { Status = SamplingObservationStatus.Unavailable }) };
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return Current;
        }
    }

    private static DeviceTopologyPort Port(string id) => new(
        "port:" + id, false, "fixture", "fixture", "fixture", null, null, null, id, null, null, null, null,
        new BackendMessage(0, 0, []), new BackendMessage(0, 0, []), null, null,
        new BackendMessage(0, 0, []), null, null, null, [], null, null, null, null, null, null, [], []);

    private sealed class Topology(string[] deviceIds, IReadOnlyList<DeviceTopologyPort>? extraPorts = null) : IDeviceTopologySnapshotProvider
    {
        public DeviceTopologySnapshotState ReadState() => DeviceTopologySnapshotState.Warming with
        {
            Snapshot = new(DateTimeOffset.UtcNow, new("fixture", "fixture", "fixture", "fixture", null, null, null),
                deviceIds.Select(Port).Concat(extraPorts ?? []).ToArray(), []),
            State = DeviceTopologySnapshotStatus.Ready
        };
        public IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("Address projection must only read the existing topology cache.");
        public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeNetworkEndpointsAsync(string id, TimeSpan interval,
            CancellationToken token, bool includeCurrent = true)
            => throw new InvalidOperationException("This consumer must not collect network endpoint information.");
        public IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeChangesAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("Address projection must not subscribe to topology changes.");
        public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("Address projection must not subscribe to collection.");
    }
}
