using System.Collections.Concurrent;
using System.Net;
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
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed partial class NvidiaFanHttpIntegrationTests
{
    [Theory]
    [InlineData(0U, 0d, 1400d)]
    [InlineData(1U, 105d, null)]
    public async Task NormalHttpCatalogCacheAndUnifiedStreamUseTheOriginalSelectedFanWorker(
        uint fanIndex, double percent, double? rpm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var recording = new NvidiaFanFixture();
        var topology = recording.Topology();
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => topology.Capture()),
            new TestEnvironment(Path.Combine(Path.GetTempPath(), $"fan-http-{Guid.NewGuid():N}")),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(timeout.Token);
        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));

        var selectedFan = new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA, fanIndex);
        var queriedTargets = new ConcurrentQueue<IReadOnlyList<NvidiaNvmlFanReadTarget>>();
        var failedQuery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredQuery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0;
        var queryCount = 0;
        var zone = new NvidiaNvmlMonitoringZone((_, _, _) =>
            throw new InvalidOperationException("No whole-GPU telemetry was selected."),
            readFanIdentities: _ => recording.Fans,
            readFans: (targets, inventory, token) =>
            {
                Assert.Same(recording.Inventory, inventory);
                var target = Assert.Single(targets);
                Assert.Equal(selectedFan, target.Fan);
                Assert.True(target.IncludePercent && target.IncludeRpm);
                queriedTargets.Enqueue(targets);
                Interlocked.Increment(ref queryCount);
                var currentPhase = Volatile.Read(ref phase);
                if (currentPhase == 1)
                {
                    failedQuery.TrySetResult(true);
                    throw new InvalidOperationException("Recorded NVML source read failure.");
                }
                if (currentPhase == 2) recoveredQuery.TrySetResult(true);
                return recording.Read(targets, inventory, token);
            });
        var registry = new MonitoringSourceZoneRegistry([new WindowsMemoryMonitoringZone(), zone]);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!,
            new WindowsGpuAdapterOrderMonitoringZone(() => recording.Inventory), null!, null!, null!, zone,
            null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using var dependencyHttp = new HttpClient();
        var controls = new UnusedControls();
        var backend = new HostReadWriteBackend(sampler, sampler, dashboard, controls, controls);
        var core = new ReadWriteCore(backend, new DependencyActions(dependencyHttp, []));
        Assert.Equal("gpu_fan", core.Catalog.Devices.Single(device => device.Identity == selectedFan.ExactIdentityKey).Resource);
        var channelEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHttpAsync(sampler, dashboard, slots.Provider, core, channelEnded, timeout.Token);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var query = "?device=" + Uri.EscapeDataString(selectedFan.ExactIdentityKey)
            + "&fields=" + NvidiaFanFixture.Percent + "," + NvidiaFanFixture.Rpm;
        var selection = MetricDeviceFields.CreateRequest(dashboard.Current!, selectedFan.ExactIdentityKey,
            [NvidiaFanFixture.Percent, NvidiaFanFixture.Rpm]);

        await AssertPublicCatalogsAsync(client, recording, selectedFan, timeout.Token);
        using (var initial = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
            Assert.All(Fields(initial.RootElement, selectedFan).Values,
                static field => Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind));
        using (var genericGet = await client.GetAsync("/api/metrics/snapshot?ids="
            + Uri.EscapeDataString(selection.Ids.First()), timeout.Token))
            Assert.Equal(HttpStatusCode.OK, genericGet.StatusCode);
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
            var current = await ReadUntilAsync(reader, value => HasPercent(value, selectedFan, percent), timeout.Token);
            AssertFanValues(current, selectedFan, percent, rpm);
            var capturedReads = Volatile.Read(ref queryCount);
            Assert.True(capturedReads > 0);
            using (var cached = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
                AssertFanValues(cached.RootElement, selectedFan, percent, rpm);

            Volatile.Write(ref phase, 1);
            await failedQuery.Task.WaitAsync(timeout.Token);
            var failed = await ReadUntilAsync(reader, value => Fields(value, selectedFan).Values.All(field =>
                field.GetProperty("numericValue").ValueKind == JsonValueKind.Null
                && field.GetProperty("status").GetInt32() == (int)SamplingObservationStatus.Unavailable), timeout.Token);
            Assert.All(Fields(failed, selectedFan).Values, static field =>
            {
                Assert.Equal("nvidia.nvml", field.GetProperty("sourceId").GetString());
                Assert.Equal(JsonValueKind.Null, field.GetProperty("observedAt").ValueKind);
            });
            using (var clearedCache = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token))
                Assert.All(Fields(clearedCache.RootElement, selectedFan).Values,
                    static field => Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind));

            Volatile.Write(ref phase, 2);
            await recoveredQuery.Task.WaitAsync(timeout.Token);
            var recovered = await ReadUntilAsync(reader, value => HasPercent(value, selectedFan, percent), timeout.Token);
            AssertFanValues(recovered, selectedFan, percent, rpm);
            await channelCancellation.CancelAsync();
            response.Dispose();
            reader.Dispose();
            // Endpoint completion includes disposal of the actual subscription enumerator.
            await channelEnded.Task.WaitAsync(timeout.Token);
            // StopAsync drains the real worker and admitted capture; no delay guesses quiescence.
            await sampler.StopAsync(timeout.Token);
            Assert.Null(sampler.CaptureSamplingWorkerTask());
            var drainedReads = Volatile.Read(ref queryCount);
            var committed = sampler.ReadLatest(selection)!.CommittedGeneration;
            for (var index = 0; index < 3; index++)
            {
                using var cached = await GetJsonAsync(client, "/api/metrics/fields/snapshot" + query, timeout.Token);
                AssertFanValues(cached.RootElement, selectedFan, percent, rpm);
                var publicField = new FieldReference(selectedFan.ExactIdentityKey, NvidiaFanFixture.Percent);
                Assert.Equal(percent, core.ReadCached(publicField).Value!.Number);
                Assert.Equal(committed, sampler.ReadLatest(selection)!.CommittedGeneration);
                Assert.Equal(drainedReads, Volatile.Read(ref queryCount));
            }
            Assert.All(queriedTargets, targets => Assert.Equal(selectedFan, Assert.Single(targets).Fan));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            await app.StopAsync(CancellationToken.None);
            await owner.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertPublicCatalogsAsync(HttpClient client, NvidiaFanFixture recording,
        NvidiaNvmlFanIdentity selected, CancellationToken token)
    {
        using var fieldCatalog = await GetJsonAsync(client, "/api/metrics/fields/catalog", token);
        var fans = fieldCatalog.RootElement.EnumerateArray().Where(device => device.GetProperty("fields").EnumerateArray()
            .Any(field => field.GetProperty("name").GetString() == NvidiaFanFixture.Percent)).ToArray();
        Assert.Equal(recording.Fans!.Select(static fan => fan.ExactIdentityKey).Order(),
            fans.Select(static device => device.GetProperty("deviceIdentity").GetString()!).Order());
        Assert.Equal(3, fans.Length);
        Assert.All(fans, device => Assert.All(device.GetProperty("fields").EnumerateArray(), field =>
        {
            Assert.Equal((int)MetricResource.Fan, field.GetProperty("resource").GetInt32());
            Assert.Contains("NVML intended", field.GetProperty("meaning").GetString());
        }));
        using var capabilityCatalog = await GetJsonAsync(client, "/api/metrics/fields/capabilities", token);
        var capabilities = capabilityCatalog.RootElement.EnumerateArray().Where(capability =>
            capability.GetProperty("field").GetProperty("deviceIdentity").GetString() == selected.ExactIdentityKey).ToArray();
        Assert.Equal(2, capabilities.Length);
        Assert.All(capabilities, capability =>
        {
            Assert.Equal("nvidia.nvml", capability.GetProperty("readSource").GetString());
            Assert.True(capability.GetProperty("read").GetProperty("supported").GetBoolean());
            Assert.False(capability.GetProperty("write").GetProperty("supported").GetBoolean());
            Assert.Equal("float64", capability.GetProperty("definition").GetProperty("valueKind").GetString());
        });
        using var catalog = await client.GetAsync("/api/metrics/catalog", token);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement value, NvidiaNvmlFanIdentity selected)
    {
        var device = Assert.Single(value.EnumerateArray());
        Assert.Equal(selected.ExactIdentityKey, device.GetProperty("deviceIdentity").GetString());
        var fields = device.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        Assert.Equal(2, fields.Count);
        return fields;
    }

    private static bool HasPercent(JsonElement value, NvidiaNvmlFanIdentity selected, double percent)
    {
        var field = Fields(value, selected)[NvidiaFanFixture.Percent];
        return field.GetProperty("numericValue").ValueKind == JsonValueKind.Number
            && field.GetProperty("numericValue").GetDouble() == percent
            && field.GetProperty("status").GetInt32() == (int)SamplingObservationStatus.Current;
    }

    private static void AssertFanValues(JsonElement value, NvidiaNvmlFanIdentity selected, double percent, double? rpm)
    {
        var fields = Fields(value, selected);
        Assert.Equal(percent, fields[NvidiaFanFixture.Percent].GetProperty("numericValue").GetDouble());
        Assert.Equal("%", fields[NvidiaFanFixture.Percent].GetProperty("unit").GetString());
        Assert.Equal("RPM", fields[NvidiaFanFixture.Rpm].GetProperty("unit").GetString());
        if (rpm is { } speed) Assert.Equal(speed, fields[NvidiaFanFixture.Rpm].GetProperty("numericValue").GetDouble());
        else Assert.Equal(JsonValueKind.Null, fields[NvidiaFanFixture.Rpm].GetProperty("numericValue").ValueKind);
        Assert.All(fields.Values, static field =>
        {
            Assert.Equal("nvidia.nvml", field.GetProperty("sourceId").GetString());
            Assert.Equal("float64", field.GetProperty("valueKind").GetString());
            Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
        });
    }

    private static async Task<JsonElement> ReadUntilAsync(StreamReader reader, Func<JsonElement, bool> accept, CancellationToken token, string expectedSubscriptionId = "fan")
    {
        for (var index = 0; index < 128; index++)
        {
            var line = await reader.ReadLineAsync(token);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line!);
            Assert.Equal(expectedSubscriptionId, document.RootElement.GetProperty("subscriptionId").GetString());
            var value = document.RootElement.GetProperty("value");
            if (accept(value)) return value.Clone();
        }
        throw new InvalidOperationException("The real channel did not publish the expected subscription state within its bounded frame count.");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private static async Task<WebApplication> StartHttpAsync(WindowsHardwareMetricSampler sampler,
        DashboardMonitoringCatalogState dashboard, RuntimePlanProvider provider, ReadWriteCore core,
        TaskCompletionSource<bool> channelEnded, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(dashboard);
        builder.Services.AddSingleton<IMetricSampler>(sampler);
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(sampler);
        builder.Services.AddSingleton<IRuntimePlanProvider>(provider);
        builder.Services.AddSingleton(core);
        // An unselected route may bind its service, but this fixture never creates that worker.
        builder.Services.AddSingleton<IGpuTelemetryWorkerClient>(_ => throw new InvalidOperationException("Unselected specialized GPU route."));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            finally { if (context.Request.Path == "/api/subscriptions/stream") channelEnded.TrySetResult(true); }
        });
        foreach (var name in new[] { "MapMetricsEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [app]);
        await app.StartAsync(token);
        return app;
    }

    private sealed class UnusedControls : IControlObjectCatalog, IControlWriteLayer
    {
        public ControlObjectCatalog ReadObjects() => new([], DateTimeOffset.UtcNow);
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
            => throw new InvalidOperationException("HTTP fan observation must never write controls.");
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => throw new InvalidOperationException("HTTP fan observation must never release controls.");
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
