using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class MetricCatalogPushTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataOnlySubscriptionPushesCapabilitySelectionAndTopologyWithoutValueDemand(bool publishProbe)
    {
        var initial = Snapshot("unverified", selected: false);
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(initial);
        var source = new SilentCatalogSource();
        await using var fixture = await Fixture.StartAsync(catalog, source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await fixture.SubscribeAsync(timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var first = await ReadAsync(reader, timeout.Token);
        var firstRevision = first.GetProperty("catalogRevision").GetString();
        Assert.Equal(catalog.Revision, firstRevision);
        AssertMetadata(first, "unverified", selected: false);

        var available = Snapshot("available", selected: true);
        if (publishProbe) catalog.PublishCatalogProbe(available);
        else Assert.True(catalog.PublishReadingMetadata(available));
        var next = await ReadAsync(reader, timeout.Token);
        Assert.NotEqual(firstRevision, next.GetProperty("catalogRevision").GetString());
        Assert.Equal(catalog.Revision, next.GetProperty("catalogRevision").GetString());
        AssertMetadata(next, "available", selected: true);

        // Selection may change without changing reading capability or preference.
        var unselected = Snapshot("available", selected: false);
        if (publishProbe) catalog.PublishCatalogProbe(unselected);
        else Assert.True(catalog.PublishReadingMetadata(unselected));
        var selectionChanged = await ReadAsync(reader, timeout.Token);
        Assert.NotEqual(next.GetProperty("catalogRevision").GetString(),
            selectionChanged.GetProperty("catalogRevision").GetString());
        AssertMetadata(selectionChanged, "available", selected: false);

        catalog.PublishCatalogProbe(unselected with
        {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>(),
            CatalogGeneration = unselected.CatalogGeneration + 1
        });
        var removed = await ReadAsync(reader, timeout.Token);
        Assert.Empty(removed.GetProperty("fields").EnumerateArray());
        Assert.Empty(CpuTemperature(removed).GetProperty("candidateSources").EnumerateArray());
        Assert.True(Assert.Single(source.Requests).IsCatalogProbe);
        Assert.Equal(0, fixture.Sampler.Reads);
        Assert.Equal(0, fixture.Sampler.Captures);
        await timeout.CancelAsync();
    }

    [Fact]
    public async Task UnchangedDiscoveryProbeRefreshesInstalledComponentProjectionAtTheSameHardwareRevision()
    {
        const string metricId = "system.motherboardTemperature";
        const string componentId = "hardware-sensors-pawnio-provider";
        var catalog = new HardwareMetricCatalogState();
        var snapshot = Snapshot("unverified", selected: false);
        catalog.PublishCatalogProbe(snapshot);
        var revision = catalog.Revision;
        var source = new ProbeCatalogSource(snapshot);
        var components = new MutableComponents();
        await using var fixture = await Fixture.StartAsync(catalog, source, components);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await fixture.SubscribeAsync(timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var first = await ReadAsync(reader, timeout.Token);
        var missing = Assert.Single(first.GetProperty("metrics").EnumerateArray(),
            metric => metric.GetProperty("id").GetString() == metricId);
        Assert.Equal(componentId, missing.GetProperty("requiredComponentId").GetString());
        Assert.Equal(BackendMessageCodes.Metric.NeedsComponent, missing.GetProperty("disabledReason").GetProperty("code").GetByte());

        components.Installed = true;
        source.Tick();
        var refreshed = await ReadAsync(reader, timeout.Token);
        var installed = Assert.Single(refreshed.GetProperty("metrics").EnumerateArray(),
            metric => metric.GetProperty("id").GetString() == metricId);
        Assert.Equal(JsonValueKind.Null, installed.GetProperty("requiredComponentId").ValueKind);
        Assert.Equal(BackendMessageCodes.Metric.NoValidReading, installed.GetProperty("disabledReason").GetProperty("code").GetByte());
        Assert.Equal(revision, refreshed.GetProperty("catalogRevision").GetString());
        Assert.Equal(revision, first.GetProperty("catalogRevision").GetString());
        Assert.Equal(first.GetProperty("fields").GetRawText(), refreshed.GetProperty("fields").GetRawText());
        Assert.Equal(revision, catalog.Revision);
        Assert.True(Assert.Single(source.Requests).IsCatalogProbe);
        Assert.Equal(0, fixture.Sampler.Reads);
        Assert.Equal(0, fixture.Sampler.Captures);
        await timeout.CancelAsync();
    }

    [Fact]
    public async Task GpuMemoryCapabilityRevisesMetadataForUnknownAbsentAndPresentWithoutValueDemand()
    {
        var baseCatalog = HardwareMetricCatalog.FromSnapshot(Snapshot("available", selected: true));
        HardwareMetricSnapshot WithCapability(bool? capability)
            => (baseCatalog with
            {
                Gpus = [new HardwareMetricGpuDescriptor(0, "GPU", "opaque-gpu", 1, "fixture")
                    { HasDedicatedMemoryMetrics = capability }]
            }).CreateSnapshot();

        var catalog = new HardwareMetricCatalogState();
        var changes = 0;
        catalog.Changed += () => changes++;
        catalog.PublishCatalogProbe(WithCapability(null));
        var source = new SilentCatalogSource();
        await using var fixture = await Fixture.StartAsync(catalog, source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await fixture.SubscribeAsync(timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        string? previousRevision = null;
        foreach (var capability in new bool?[] { null, false, true })
        {
            var snapshot = WithCapability(capability);
            catalog.PublishCatalogProbe(snapshot);
            var envelope = await ReadAsync(reader, timeout.Token);
            var revision = envelope.GetProperty("catalogRevision").GetString();
            Assert.Equal(catalog.Revision, revision);
            Assert.NotEqual(previousRevision, revision);
            var gpuMetrics = envelope.GetProperty("metrics").EnumerateArray()
                .Where(metric => metric.GetProperty("scopeKind").GetString() == "gpu").ToArray();
            Assert.NotEmpty(gpuMetrics);
            Assert.All(gpuMetrics, metric =>
            {
                var fact = metric.GetProperty("hasDedicatedMemoryMetrics");
                Assert.Equal(capability, fact.ValueKind == JsonValueKind.Null ? (bool?)null : fact.GetBoolean());
            });
            Assert.Equal(capability, Assert.Single(catalog.ReadCommitted().Snapshot!.Gpus).HasDedicatedMemoryMetrics);

            // Neither repeated metadata nor newly sampled numbers can change the capability revision.
            catalog.PublishCatalogProbe(snapshot);
            catalog.PublishCatalogProbe(snapshot with
            {
                Gpus = [snapshot.Gpus[0] with { UsagePercent = 82, IsUsageAvailable = true }],
                Items = new Dictionary<string, MetricReadingValue> { ["cpu.usage"] = new("cpu.usage", 42, "%") }
            });
            Assert.Equal(revision, catalog.Revision);
            previousRevision = revision;
        }
        Assert.Equal(3, changes);
        Assert.True(Assert.Single(source.Requests).IsCatalogProbe);
        Assert.Equal(0, fixture.Sampler.Reads);
        Assert.Equal(0, fixture.Sampler.Captures);
        await timeout.CancelAsync();
    }

    [Fact]
    public async Task CatalogGetsWithoutCommittedMetadataDoNotStartDiscoveryOrSampling()
    {
        var source = new SilentCatalogSource();
        await using var fixture = await Fixture.StartAsync(new HardwareMetricCatalogState(), source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fields = await fixture.Client.GetAsync("/api/metrics/fields/catalog", timeout.Token);
        using var metrics = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/metrics/catalog", timeout.Token));

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, fields.StatusCode);
        Assert.Empty(metrics.RootElement.EnumerateArray());
        Assert.Empty(source.Requests);
        Assert.Equal(0, fixture.Sampler.Reads);
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Fact]
    public async Task CatalogGetsOnlyReadCommittedStateAndMatchTheMetadataEnvelope()
    {
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(Snapshot("available", selected: true));
        var committed = catalog.ReadCommitted();
        var source = new SilentCatalogSource();
        await using var fixture = await Fixture.StartAsync(catalog, source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fields = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/metrics/fields/catalog", timeout.Token));
        using var metrics = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/metrics/catalog", timeout.Token));
        using var response = await fixture.SubscribeAsync(timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var envelope = await ReadAsync(reader, timeout.Token);
        Assert.Equal(committed.Revision, envelope.GetProperty("catalogRevision").GetString());
        Assert.Equal(fields.RootElement.GetRawText(), envelope.GetProperty("fields").GetRawText());
        Assert.Equal(metrics.RootElement.GetRawText(), envelope.GetProperty("metrics").GetRawText());
        Assert.Equal(0, fixture.Sampler.Reads);
        Assert.Equal(0, fixture.Sampler.Captures);
        Assert.True(Assert.Single(source.Requests).IsCatalogProbe);
        await timeout.CancelAsync();
    }

    private static HardwareMetricSnapshot Snapshot(string availability, bool selected)
    {
        const string name = "cpu_all_temperature_reported_celsius";
        var field = MetricFieldRegistry.FindByName(name)!;
        var binding = new MetricDeviceFieldBinding("opaque-cpu", field with
        {
            Reading = new("reading-cpu-temperature", "cpu.temperature", 10, availability,
                availability == "available", availability == "available" ? "opaque-cpu" : null,
                availability == "available" ? name : null)
        }, "raw-cpu-temperature", "windows.wmi")
        {
            RequestAliases = ["cpu.temperature"],
            SelectedRequestAliases = selected ? ["cpu.temperature"] : []
        };
        return new HardwareMetricCatalog(new("CPU", 0, ""), [],
            new Dictionary<string, HardwareMetricItemDescriptor>(),
            new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding },
            1, 1, 1).CreateSnapshot();
    }

    private static void AssertMetadata(JsonElement envelope, string availability, bool selected)
    {
        var field = Assert.Single(Assert.Single(envelope.GetProperty("fields").EnumerateArray())
            .GetProperty("fields").EnumerateArray());
        Assert.Equal(availability, field.GetProperty("reading").GetProperty("sourceAvailability").GetString());
        var candidate = Assert.Single(CpuTemperature(envelope).GetProperty("candidateSources").EnumerateArray());
        Assert.Equal("opaque-cpu", candidate.GetProperty("deviceIdentity").GetString());
        Assert.Equal(field.GetProperty("name").GetString(), candidate.GetProperty("fieldName").GetString());
        Assert.Equal("windows.wmi", candidate.GetProperty("sourceId").GetString());
        Assert.Equal(availability, candidate.GetProperty("sourceAvailability").GetString());
        Assert.Equal(selected, candidate.GetProperty("isSelected").GetBoolean());
        Assert.Equal(field.GetProperty("reading").GetRawText(), candidate.GetProperty("reading").GetRawText());
        Assert.False(field.TryGetProperty("numericValue", out _));
    }

    private static JsonElement CpuTemperature(JsonElement envelope)
        => Assert.Single(envelope.GetProperty("metrics").EnumerateArray(),
            metric => metric.GetProperty("id").GetString() == "cpu.temperature");

    private static async Task<JsonElement> ReadAsync(StreamReader reader, CancellationToken token)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(token))!);
        Assert.Equal("catalog", document.RootElement.GetProperty("subscriptionId").GetString());
        return document.RootElement.GetProperty("value").Clone();
    }

    // Discovery stays open and emits no values: only committed metadata can wake the stream.
    private sealed class SilentCatalogSource : IMetricSnapshotPushSource
    {
        public List<MetricSampleRequest> Requests { get; } = [];
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }

    private sealed class NoIoSampler : IMetricSampler
    {
        public int Reads;
        public int Captures;
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            throw new InvalidOperationException("Catalog transport must read committed metadata.");
        }
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Captures);
            throw new InvalidOperationException("Catalog transport must not collect device values.");
        }
    }

    private sealed class ProbeCatalogSource(HardwareMetricSnapshot snapshot) : IMetricSnapshotPushSource
    {
        private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();
        public List<MetricSampleRequest> Requests { get; } = [];
        public void Tick() => ticks.Writer.TryWrite(true);
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await foreach (var _ in ticks.Reader.ReadAllAsync(token)) yield return snapshot;
        }
    }

    private sealed class MutableComponents : IComponentManager
    {
        private readonly IComponentManager empty = new NoInstalledComponentManager();
        public bool Installed { get; set; }
        public Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<ComponentStatus>>([new(
                ComponentCatalog.Definitions.Single(definition => definition.Id == "hardware-sensors-pawnio-provider"),
                "fixture", "", "", null, false, Installed, false, false, false, false, [], new BackendMessage(0, 0, []))]);
        public Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken token) => empty.GetStatusAsync(id, token);
        public Task<ComponentActionResult> DownloadAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken token, IProgress<DependencyDownloadProgress>? progress = null)
            => empty.DownloadAsync(id, acknowledgeExternalTerms, versionChoice, token, progress);
        public Task<ComponentActionResult> InstallAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken token, SoftwareOperationCaller? caller = null, IProgress<DependencyDownloadProgress>? progress = null)
            => empty.InstallAsync(id, acknowledgeExternalTerms, versionChoice, token, caller, progress);
        public Task<ComponentActionResult> VerifyAsync(string id, CancellationToken token) => empty.VerifyAsync(id, token);
    }

    private sealed record Fixture(WebApplication App, HttpClient Client, NoIoSampler Sampler) : IAsyncDisposable
    {
        public static async Task<Fixture> StartAsync(HardwareMetricCatalogState catalog, IMetricSnapshotPushSource source,
            IComponentManager? components = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read");
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
            var sampler = new NoIoSampler();
            builder.Services.AddSingleton(catalog);
            builder.Services.AddSingleton<IMetricSnapshotPushSource>(source);
            builder.Services.AddSingleton<IMetricSampler>(sampler);
            builder.Services.AddSingleton<IComponentManager>(components ?? new NoInstalledComponentManager());
            builder.Services.AddSingleton<ReadWriteCore>(_ => throw new InvalidOperationException("Unused capability route."));
            builder.Services.AddSingleton<IGpuTelemetryPushSource>(_ => throw new InvalidOperationException("Catalog routes must not resolve a GPU subscription source."));
            var app = TrustedAccessEndpointTestAuthentication.Build(builder);
            foreach (var name in new[] { "MapMetricsEndpoints", "MapSubscriptionChannelEndpoints" })
                typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name,
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
            await app.StartAsync();
            return new(app, TrustedAccessEndpointTestAuthentication.Client(app), sampler);
        }

        public Task<HttpResponseMessage> SubscribeAsync(CancellationToken token)
            => Client.SendAsync(new(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "catalog", path = "/api/metrics/fields/catalog/subscribe?intervalMs=100" } } })
            }, HttpCompletionOption.ResponseHeadersRead, token);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
