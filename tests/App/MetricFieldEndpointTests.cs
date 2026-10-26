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
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class MetricFieldEndpointTests
{
    [Theory]
    [InlineData("fan_all_rotational_speed", "fan_all_speed_rpm")]
    [InlineData("fan_all_pwm_reported", "fan_all_reported_pwm_duty")]
    public void NotebookFanAliasesResolveToOneCanonicalPublicField(string alias, string canonical)
    {
        Assert.Equal(canonical, MetricFieldRegistry.NormalizePublicName(alias));
        Assert.Equal(canonical, MetricFieldRegistry.FindByName(alias)?.Name);
    }

    [Fact]
    public void FanAliasAndCanonicalNameSelectTheSameSingleObservation()
    {
        var seed = NetworkCounterInformationCollectorTests.CommittedSnapshot();
        var binding = new MetricDeviceFieldBinding("mechrevo|uwacpi|fan|cpu",
            MetricFieldRegistry.FindByName("fan_all_speed_rpm")!, "fan/rpm", "notebook-oem.fan");
        var snapshot = seed with
        {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [binding.RequestAddress] = binding }
        };
        var request = MetricDeviceFields.CreateRequest(snapshot, binding.DeviceIdentity,
            ["fan_all_rotational_speed", "fan_all_speed_rpm"]);

        Assert.Equal([binding.RequestAddress], request.Ids);
        var read = Assert.Single(MetricDeviceFields.Read(snapshot, binding.DeviceIdentity,
            new HashSet<string>(["fan_all_rotational_speed"], StringComparer.Ordinal)));
        Assert.Equal("fan_all_speed_rpm", Assert.Single(read.Fields).Name);
    }

    [Fact]
    public async Task NetworkCountersAndActualRateWindowReachCachedHttpAndUnifiedPush()
    {
        var snapshot=NetworkCounterInformationCollectorTests.CommittedSnapshot();
        var source=new Source(snapshot);var catalog=new HardwareMetricCatalogState();catalog.PublishCatalogProbe(snapshot);
        await using var app=await StartAsync(source,catalog);using var client=CreateClient(app);
        var device=snapshot.FieldBindings.Values.First(binding=>binding.Field.Name=="network_all_rx_errors_actual").DeviceIdentity;
        var query="?device="+Uri.EscapeDataString(device)+"&fields=network_all_rx_errors_actual,network_all_rx_bytes_per_second_actual";
        using var cached=JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot"+query));Check(cached.RootElement[0].GetProperty("fields"),false);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream") { Content=JsonContent.Create(new { version=1,subscriptions=new[] { new { id="network",path="/api/metrics/subscribe"+query } } }) };
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var empty=JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);Check(empty.RootElement.GetProperty("value")[0].GetProperty("fields"),true);
        using var current=JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);Check(current.RootElement.GetProperty("value")[0].GetProperty("fields"),false);
        Assert.Equal(1,source.ReadCount);Assert.Equal(2,source.Request!.Ids.Count);
        await timeout.CancelAsync();response.Dispose();await app.StopAsync();
        static void Check(JsonElement array,bool missing)
        {
            var fields=array.EnumerateArray().ToDictionary(f=>f.GetProperty("name").GetString()!);
            var counter=fields["network_all_rx_errors_actual"];var rate=fields["network_all_rx_bytes_per_second_actual"];
            Assert.Equal("unsigned64",counter.GetProperty("valueKind").GetString());Assert.Equal(missing?null:"18446744073709551615",counter.GetProperty("integerValue").GetString());
            Assert.Equal("float64",rate.GetProperty("valueKind").GetString());Assert.Equal("B/s",rate.GetProperty("unit").GetString());
            Assert.Equal(missing?JsonValueKind.Null:JsonValueKind.Number,rate.GetProperty("numericValue").ValueKind);
            if(!missing) { Assert.Equal(200,rate.GetProperty("numericValue").GetDouble());Assert.Equal(1000UL,rate.GetProperty("sampleDurationMilliseconds").GetUInt64()); }
            Assert.All(fields.Values,f=>Assert.Equal("windows.network-interface",f.GetProperty("sourceId").GetString()));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BooleanFieldsReachActualCachedHttpAndUnifiedPushWithoutNumericCoercion(bool flag)
    {
        var snapshot = IntelMsrThermalCollectorTests.CommittedStatusSnapshot(flag);
        var binding = Assert.Single(snapshot.FieldBindings.Values, static binding => binding.Field.Name == "cpu_intel_core_thermal_throttling_active");
        var source = new Source(snapshot); var catalog = new HardwareMetricCatalogState(); catalog.PublishCatalogProbe(snapshot);
        await using var app = await StartAsync(source, catalog); using var client = CreateClient(app);
        var query = "?device=" + Uri.EscapeDataString(binding.DeviceIdentity) + "&fields=" + binding.Field.Name;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query));
        Check(cached.RootElement[0].GetProperty("fields")[0], flag);
        Assert.Equal(1, source.ReadCount); Assert.Equal([binding.RequestAddress], source.Request!.Ids);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "boolean", path = "/api/metrics/subscribe" + query } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var empty = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Check(empty.RootElement.GetProperty("value")[0].GetProperty("fields")[0], null);
        using var recovered = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Check(recovered.RootElement.GetProperty("value")[0].GetProperty("fields")[0], flag);
        Assert.Equal(1, source.ReadCount);
        await timeout.CancelAsync(); response.Dispose(); await app.StopAsync();

        static void Check(JsonElement field, bool? expected)
        {
            Assert.Equal("boolean", field.GetProperty("valueKind").GetString());
            Assert.Equal("intel.msr", field.GetProperty("sourceId").GetString());
            Assert.Equal(expected, field.GetProperty("booleanValue").ValueKind == JsonValueKind.Null ? (bool?)null : field.GetProperty("booleanValue").GetBoolean());
            foreach (var name in new[] { "numericValue", "integerValue", "bytesValue" }) Assert.Equal(JsonValueKind.Null, field.GetProperty(name).ValueKind);
        }
    }

    [Fact]
    public async Task AtaRawPagesUseCacheOnlyHttpAndUnifiedPushWithoutLosingBytesOrSource()
    {
        var snapshot = AtaSmartInformationCollectorTests.CommittedSnapshot();
        var source = new Source(snapshot);
        var catalog = new HardwareMetricCatalogState(); catalog.PublishCatalogProbe(snapshot);
        await using var app = await StartAsync(source, catalog);
        using var client = CreateClient(app);
        var query = "?device=" + Uri.EscapeDataString(AtaSmartInformationCollectorTests.Device)
            + "&fields=" + AtaSmartInformationCollectorTests.Data + "," + AtaSmartInformationCollectorTests.Thresholds;
        using var current = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query));
        var values = current.RootElement[0].GetProperty("fields").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!);
        Assert.Equal(2, values.Count);
        foreach (var (name, seed) in new[] { (AtaSmartInformationCollectorTests.Data, 7), (AtaSmartInformationCollectorTests.Thresholds, 13) })
        {
            var value = values[name];
            Assert.Equal("windows.ata-smart", value.GetProperty("sourceId").GetString());
            Assert.Equal("bytes", value.GetProperty("valueKind").GetString());
            Assert.Equal(AtaSmartInformationCollectorTests.Page(seed), value.GetProperty("bytesValue").GetBytesFromBase64());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("integerValue").ValueKind);
            Assert.Equal(JsonValueKind.Null, value.GetProperty("numericValue").ValueKind);
        }
        Assert.Equal(1, source.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "ata", path = "/api/metrics/subscribe" + query } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var failed = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.All(failed.RootElement.GetProperty("value")[0].GetProperty("fields").EnumerateArray(),
            value => Assert.Equal(JsonValueKind.Null, value.GetProperty("bytesValue").ValueKind));
        using var recovered = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var pushed = recovered.RootElement.GetProperty("value")[0].GetProperty("fields").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!);
        Assert.Equal(AtaSmartInformationCollectorTests.Page(13), pushed[AtaSmartInformationCollectorTests.Thresholds].GetProperty("bytesValue").GetBytesFromBase64());
        Assert.Equal(1, source.ReadCount);
        await timeout.CancelAsync(); response.Dispose(); await app.StopAsync();
    }

    [Fact]
    public async Task CataloguePushUsesExistingDemandChannelBeforeDirectoryIsReady()
    {
        var catalog = new HardwareMetricCatalogState();
        var source = new Source { CommitCatalog = catalog.PublishCatalogProbe };
        Assert.Null(catalog.Current);
        await using var app = await StartAsync(source, catalog);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "directory", path = "/api/metrics/fields/catalog/subscribe?intervalMs=1000" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var pushed = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var envelope = pushed.RootElement.GetProperty("value");
        Assert.Equal(catalog.Revision, envelope.GetProperty("catalogRevision").GetString());
        Assert.Equal("memory", envelope.GetProperty("fields")[0].GetProperty("deviceIdentity").GetString());
        Assert.NotEmpty(envelope.GetProperty("metrics").EnumerateArray());
        Assert.True(source.Request!.IsCatalogProbe);
        Assert.Equal(0, source.ReadCount);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task StorageFieldsReachCachedHttpAndUnifiedPushWithExactIntegerAndSource()
    {
        var source = new Source(StorageInformationCollectorTests.CommittedSnapshot());
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(source.Current);
        await using var app = await StartAsync(source, catalog);
        using var client = CreateClient(app);
        var query = "?device=" + Uri.EscapeDataString(StorageInformationCollectorTests.Identity)
            + "&fields=storage_all_read_errors_total_reported,storage_all_nvme_percentage_used_reported,storage_all_nvme_media_errors_reported,storage_all_nvme_health_log_raw";
        using var response = await client.GetAsync("/api/metrics/fields/snapshot" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var current = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var values = current.RootElement[0].GetProperty("fields").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!);
        Assert.Equal("9223372036854775811", values["storage_all_read_errors_total_reported"].GetProperty("integerValue").GetString());
        Assert.Equal("windows.storage-sensors", values["storage_all_read_errors_total_reported"].GetProperty("sourceId").GetString());
        Assert.Equal("255", values["storage_all_nvme_percentage_used_reported"].GetProperty("integerValue").GetString());
        Assert.Equal("windows.nvme-health", values["storage_all_nvme_percentage_used_reported"].GetProperty("sourceId").GetString());
        var wide = values["storage_all_nvme_media_errors_reported"];
        Assert.Equal(UInt128.MaxValue.ToString(), wide.GetProperty("integerValue").GetString());
        Assert.Equal(JsonValueKind.Null, wide.GetProperty("numericValue").ValueKind);
        Assert.Equal("unsigned128", wide.GetProperty("valueKind").GetString());
        var raw = values["storage_all_nvme_health_log_raw"];
        var rawBytes = raw.GetProperty("bytesValue").GetBytesFromBase64();
        Assert.Equal(512, rawBytes.Length);
        Assert.Equal(Enumerable.Repeat((byte)255, 160), rawBytes.Skip(32).Take(160));
        Assert.Equal("bytes", raw.GetProperty("valueKind").GetString());
        Assert.Equal(1, source.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pushRequest = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "storage", path = "/api/metrics/subscribe" + query } } })
        };
        using var push = await client.SendAsync(pushRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        using var stream = new StreamReader(await push.Content.ReadAsStreamAsync(timeout.Token));
        using var empty = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        using var recovered = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.All(empty.RootElement.GetProperty("value")[0].GetProperty("fields").EnumerateArray(), value =>
        {
            Assert.Equal(JsonValueKind.Null, value.GetProperty("integerValue").ValueKind);
            Assert.Equal(JsonValueKind.Null, value.GetProperty("bytesValue").ValueKind);
        });
        var recoveredValues = recovered.RootElement.GetProperty("value")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!);
        Assert.Equal(4, recoveredValues.Count);
        Assert.Equal(UInt128.MaxValue.ToString(), recoveredValues["storage_all_nvme_media_errors_reported"].GetProperty("integerValue").GetString());
        Assert.Equal(rawBytes, recoveredValues["storage_all_nvme_health_log_raw"].GetProperty("bytesValue").GetBytesFromBase64());
        Assert.Equal(1, source.ReadCount); // Push does not call read or capture.
        await timeout.CancelAsync();
        push.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task GetReadsCachedValueAndValidatesDeviceAndFields()
    {
        var source = new Source();
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(source.Current);
        await using var app = await StartAsync(source, catalog);
        using var client = CreateClient(app);
        using var directory = await client.GetAsync("/api/metrics/fields/catalog");
        Assert.Equal(HttpStatusCode.OK, directory.StatusCode);
        using var json = JsonDocument.Parse(await directory.Content.ReadAsStringAsync());
        Assert.Equal("memory", json.RootElement[0].GetProperty("deviceIdentity").GetString());
        Assert.Equal("memory_all_physical_available_bytes", json.RootElement[0].GetProperty("fields")[0].GetProperty("name").GetString());
        using var result = await client.GetAsync(FieldPath);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var value = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(1024, value.RootElement[0].GetProperty("fields")[0].GetProperty("numericValue").GetDouble());
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(["memory-scope/memory_all_physical_available_bytes"], source.Request!.Ids);
        foreach (var path in new[] { "/api/metrics/fields/snapshot", FieldPath.Replace("memory&", "unknown&"), FieldPath + ",typo" })
        {
            using var rejected = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        Assert.Equal(1, source.ReadCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task PushUsesSameBoundRequestAndReportsUnavailableThenRecovery()
    {
        var source = new Source();
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(source.Current);
        await using var app = await StartAsync(source, catalog);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") { Content = JsonContent.Create(ChannelRequest) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var unavailable = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        using var current = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var empty = unavailable.RootElement.GetProperty("value")[0].GetProperty("fields")[0];
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("numericValue").ValueKind);
        var populated = current.RootElement.GetProperty("value")[0].GetProperty("fields")[0];
        Assert.Equal(1024, populated.GetProperty("numericValue").GetDouble());
        Assert.Equal("windows.memory", populated.GetProperty("sourceId").GetString());
        Assert.Equal("memory_all_physical_available_bytes", populated.GetProperty("name").GetString());
        Assert.Equal(["memory-scope/memory_all_physical_available_bytes"], source.Request!.Ids);
        Assert.Equal(0, source.ReadCount);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task UnreadyCatalogReturns503ForReadsAndPushWithoutStartingDemand()
    {
        var source = new Source();
        await using var app = await StartAsync(source, new());
        using var client = CreateClient(app);
        foreach (var path in new[] { "/api/metrics/fields/catalog", FieldPath })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        using var push = await client.PostAsJsonAsync("/api/subscriptions/stream", ChannelRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, push.StatusCode);
        Assert.Null(source.Request);
        Assert.Equal(0, source.ReadCount);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("?device=&fields=memory_all_physical_available_bytes")]
    [InlineData("?device=memory&fields=typo")]
    [InlineData("?device=unknown&fields=memory_all_physical_available_bytes")]
    [InlineData("?device=memory&device=unknown&fields=memory_all_physical_available_bytes")]
    public async Task MalformedFieldPushCannotFallBackToLegacyDefault(string query)
    {
        var source = new Source();
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(source.Current);
        await using var app = await StartAsync(source,catalog);
        using var client = CreateClient(app);
        using var response = await client.PostAsJsonAsync("/api/subscriptions/stream",new { version=1,
            subscriptions=new[] { new { id="bad",path="/api/metrics/subscribe"+query } } });
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Null(source.Request);
        await app.StopAsync();
    }

    private const string FieldPath = "/api/metrics/fields/snapshot?device=memory&fields=memory_all_physical_available_bytes";
    private static readonly object ChannelRequest = new { version = 1, subscriptions = new[]
        { new { id = "fields", path = "/api/metrics/subscribe?device=memory&fields=memory_all_physical_available_bytes" } } };

    private static async Task<WebApplication> StartAsync(Source source, HardwareMetricCatalogState catalog)
    {
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IMetricSampler>(source);
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(source);
        builder.Services.AddSingleton<IComponentManager, NoInstalledComponentManager>();
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var name in new[] { "MapMetricFieldEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app) => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient()
    {
        BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(10)
    }, app.Services);

    private sealed class Source(HardwareMetricSnapshot? snapshot = null) : IMetricSampler, IMetricSnapshotPushSource
    {
        internal HardwareMetricSnapshot Current { get; } = snapshot ?? MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current);
        internal int ReadCount { get; private set; }
        internal MetricSampleRequest? Request { get; private set; }
        internal Action<HardwareMetricSnapshot>? CommitCatalog { get; init; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        {
            Assert.False(request.IsCatalogProbe);
            Request = request;
            ReadCount++;
            return Task.FromResult(Current);
        }
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => throw new InvalidOperationException("A field read must not trigger capture.");
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            Request = request;
            if (request.IsCatalogProbe) CommitCatalog?.Invoke(Current);
            yield return Current with { Items = new Dictionary<string, MetricReadingValue>(), Datasets = Current.Datasets.ToDictionary(
                pair => pair.Key, pair => pair.Value with { Status = SamplingObservationStatus.Unavailable }) };
            await Task.Yield();
            yield return Current;
        }
    }
}
