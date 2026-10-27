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

public sealed partial class AcpiInformationSubscriptionTests
{
    [Fact]
    public async Task FailureKeepsDeclarationsClearsCurrentRecoversAndDoesNotPersistAcpi()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables(), null,
            new InvalidOperationException("unavailable"), AcpiFirmwareTestData.Tables(7), Array.Empty<AcpiFirmwareTable>()));
        Assert.Equal(DeviceTopologySampleCompletion.Sampled, await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None));
        var current = fixture.Provider.ReadState().AcpiInformationFields;
        Assert.Equal("6", AcpiFirmwareTestData.Field(current, "firmware_all_acpi_table_revision").Value!.Value.GetString());
        Assert.Equal(DeviceTopologySampleCompletion.Failed, await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None));
        var failed = fixture.Provider.ReadState().AcpiInformationFields;
        Assert.Null(failed.ObservedAt);
        Assert.Equal(Assert.Single(current.Devices).DeviceIdentity, Assert.Single(failed.Devices).DeviceIdentity);
        Assert.All(Assert.Single(failed.Devices).Fields, field => Assert.Null(field.Value));
        Assert.Equal(DeviceTopologySampleCompletion.Failed, await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None));
        await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None);
        var recovered = fixture.Provider.ReadState().AcpiInformationFields;
        Assert.Equal("7", AcpiFirmwareTestData.Field(recovered, "firmware_all_acpi_table_revision").Value!.Value.GetString());
        await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None);
        Assert.Empty(fixture.Provider.ReadState().AcpiInformationFields.Devices);
        Assert.Equal(0, fixture.Topology.ReadCount);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(DeviceTopologySnapshotStatus.Warming, fixture.Provider.ReadState().State);
    }

    [Fact]
    public async Task PublicationSignalsAndCaptureTimesAreIndependentForBothLogicalItems()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var topology = fixture.Provider.SubscribeAsync("ordinary", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        await using var acpi = fixture.Provider.SubscribeAcpiAsync("detail", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await topology.MoveNextAsync()); // Immediate cached current, no capture.
        var topologyNext = topology.MoveNextAsync().AsTask();
        var acpiNext = acpi.MoveNextAsync().AsTask();
        await fixture.Provider.SampleAcpiNowAsync(timeout.Token);
        Assert.True(await acpiNext);
        Assert.False(topologyNext.IsCompleted);
        Assert.Same(fixture.Provider.ReadState().AcpiInformationFields, acpi.Current);
        var acpiCurrent = acpi.Current;
        var acpiNextAgain = acpi.MoveNextAsync().AsTask();
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await topologyNext);
        Assert.False(acpiNextAgain.IsCompleted);
        Assert.Same(acpiCurrent, fixture.Provider.ReadState().AcpiInformationFields);
        var baseCurrent = fixture.Provider.ReadState().InformationFields;
        await fixture.Provider.SampleAcpiNowAsync(timeout.Token);
        Assert.True(await acpiNextAgain);
        Assert.Same(baseCurrent, fixture.Provider.ReadState().InformationFields);
        await timeout.CancelAsync();
    }

    [Fact]
    public async Task HighestDemandAppliesPerItemAndCancellationRestoresSlowFrequency()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var fastCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await fixture.Provider.StartAsync(timeout.Token);
        try
        {
            // Keep the slow sample beyond the cancellation assertions.
            // Its deadline is relative to its own last completed capture.
            var slowInterval = TimeSpan.FromMinutes(1);
            await using var slow = fixture.Provider.SubscribeAcpiAsync("slow", slowInterval, timeout.Token).GetAsyncEnumerator();
            Assert.True(await slow.MoveNextAsync());
            var initial = slow.Current;
            await using var topology = fixture.Provider.SubscribeAsync("ordinary", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
            Assert.True(await topology.MoveNextAsync());
            Assert.True(await topology.MoveNextAsync());
            Assert.True(await topology.MoveNextAsync());
            Assert.Equal(1, fixture.Acpi.ReadCount);
            Assert.Same(initial, fixture.Provider.ReadState().AcpiInformationFields);
            var fast = fixture.Provider.SubscribeAcpiAsync("fast", TimeSpan.FromSeconds(1), fastCancellation.Token).GetAsyncEnumerator();
            try
            {
                Assert.True(await fast.MoveNextAsync());
                Assert.True(await fast.MoveNextAsync());
                Assert.True(await fast.MoveNextAsync());
                Assert.InRange(fixture.Acpi.ReadCount, 4, 5);
            }
            finally
            {
                await fastCancellation.CancelAsync();
                await fast.DisposeAsync();
            }
            var count = fixture.Acpi.ReadCount;
            var current = fixture.Provider.ReadState().AcpiInformationFields;
            Assert.True(await topology.MoveNextAsync());
            Assert.True(await topology.MoveNextAsync());
            await Task.Delay(300, timeout.Token);
            Assert.Equal(count, fixture.Acpi.ReadCount);
            Assert.Same(current, fixture.Provider.ReadState().AcpiInformationFields);
        }
        finally { await fixture.Provider.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancelDuringSynchronousAcpiReadAllowsAtomicCompletionAndStopsFutureReads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(new AcpiReader(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return AcpiFirmwareTestData.Tables();
        }));
        await fixture.Provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var subscription = fixture.Provider.SubscribeAcpiAsync("in-flight", TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        try
        {
            var before = fixture.Provider.ReadState();
            var next = subscription.MoveNextAsync().AsTask();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Same(before, fixture.Provider.ReadState());
            Assert.False(next.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            await subscription.DisposeAsync();
            release.Set();
            var until = DateTimeOffset.UtcNow.AddSeconds(2);
            while (fixture.Provider.ReadState().AcpiInformationFields.ObservedAt is null && DateTimeOffset.UtcNow < until)
                await Task.Delay(20);
            Assert.NotNull(fixture.Provider.ReadState().AcpiInformationFields.ObservedAt);
            await Task.Delay(1300);
            Assert.Equal(1, fixture.Acpi.ReadCount);
            Assert.Equal(0, fixture.Topology.ReadCount);
        }
        finally { release.Set(); await fixture.Provider.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AlreadyCanceledCaptureDoesNotReadFirmware()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await fixture.Provider.SampleAcpiNowAsync(cancellation.Token));
        Assert.Equal(0, fixture.Acpi.ReadCount);
    }

    [Fact]
    public async Task HttpGetAcquiresFreshAcpiAndLegacyTopologyKeepsItsIndependentScope()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var first = await client.GetFromJsonAsync<JsonDocument>("/api/device-topology/acpi/fields");
        Assert.Equal("FACP", WireField(first!.RootElement, "firmware_all_acpi_table_signature").GetString());
        Assert.Equal(1, fixture.Acpi.ReadCount);
        Assert.Equal(0, fixture.Topology.ReadCount);
        using var response = await client.GetAsync("/api/device-topology/acpi/fields");
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        using var current = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FACP", WireField(current.RootElement, "firmware_all_acpi_table_signature").GetString());
        Assert.Equal(AcpiFirmwareTestData.Raw(AcpiFirmwareTestData.Id("FACP")),
            WireField(current.RootElement, "firmware_all_acpi_table_raw").GetBytesFromBase64());
        using var legacy = await client.GetFromJsonAsync<JsonDocument>("/api/device-topology/state");
        Assert.False(legacy!.RootElement.TryGetProperty("acpiInformationFields", out _));
        Assert.Equal(2, fixture.Acpi.ReadCount);
        Assert.Equal(1, fixture.Topology.ReadCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedHttpSubscriptionPublishesRecoveryAndStopsWithoutDemand()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables(), null, AcpiFirmwareTestData.Tables(7)));
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "acpi", path = "/api/device-topology/acpi/fields/subscribe?intervalMs=1000" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var first = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        using var failure = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        using var recovery = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.Equal("6", WireField(first.RootElement.GetProperty("value"), "firmware_all_acpi_table_revision").GetString());
        Assert.Equal(JsonValueKind.Null, WireField(failure.RootElement.GetProperty("value"), "firmware_all_acpi_table_raw").ValueKind);
        Assert.Equal(JsonValueKind.Null, failure.RootElement.GetProperty("value").GetProperty("observedAt").ValueKind);
        Assert.Equal("7", WireField(recovery.RootElement.GetProperty("value"), "firmware_all_acpi_table_revision").GetString());
        await timeout.CancelAsync();
        response.Dispose();
        await Task.Delay(250);
        var count = fixture.Acpi.ReadCount;
        var current = fixture.Provider.ReadState().AcpiInformationFields;
        await Task.Delay(1300);
        Assert.Equal(count, fixture.Acpi.ReadCount);
        Assert.Same(current, fixture.Provider.ReadState().AcpiInformationFields);
        Assert.Equal(0, fixture.Topology.ReadCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task OrdinaryTopologyFailureDoesNotClearTheIndependentAcpiCurrentValue()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        await fixture.Provider.SampleAcpiNowAsync(CancellationToken.None);
        var acpi = fixture.Provider.ReadState().AcpiInformationFields;
        fixture.Topology.Fail = true;
        Assert.Equal(DeviceTopologySampleCompletion.Failed, await fixture.Provider.SampleNowAsync(CancellationToken.None));
        Assert.Null(fixture.Provider.ReadState().InformationFields.ObservedAt);
        Assert.Same(acpi, fixture.Provider.ReadState().AcpiInformationFields);
    }

    [Fact]
    public async Task CanceledAcpiDemandDoesNotStartItsReadAfterACoDueTopologyCapture()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        fixture.Topology.Capture = () => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return DeviceInformationTestData.Snapshot();
        };
        using var baseCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var acpiCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var topology = fixture.Provider.SubscribeAsync("base", TimeSpan.FromSeconds(10), baseCancellation.Token).GetAsyncEnumerator();
        var acpi = fixture.Provider.SubscribeAcpiAsync("acpi", TimeSpan.FromSeconds(10), acpiCancellation.Token).GetAsyncEnumerator();
        Assert.True(await topology.MoveNextAsync()); // Immediate cached current.
        var baseNext = topology.MoveNextAsync().AsTask();
        var acpiNext = acpi.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await acpiCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acpiNext);
            await acpi.DisposeAsync();
            release.Set();
            Assert.True(await baseNext);
            await Task.Delay(250);
            Assert.Equal(1, fixture.Topology.ReadCount);
            Assert.Equal(0, fixture.Acpi.ReadCount);
            Assert.Null(fixture.Provider.ReadState().AcpiInformationFields.ObservedAt);
            await baseCancellation.CancelAsync();
        }
        finally { release.Set(); await fixture.Provider.StopAsync(CancellationToken.None); }
    }

    private static JsonElement WireField(JsonElement snapshot, string name)
        => Assert.Single(Assert.Single(snapshot.GetProperty("devices").EnumerateArray()).GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("definition").GetProperty("name").GetString() == name).GetProperty("value");

    private static async Task<WebApplication> StartAsync(DeviceTopologySnapshotProvider provider)
    {
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(provider);
        builder.Services.AddHostedService(_ => provider);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var name in new[] { "MapDeviceTopologyEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) {
        BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(15)
    }, app.Services);

    private sealed class AcpiReader : IAcpiFirmwareTableReader
    {
        private readonly Func<IReadOnlyList<AcpiFirmwareTable>?> read;
        private int count;
        public int ReadCount => Volatile.Read(ref count);
        public AcpiReader(params object?[] results)
        {
            var queue = new Queue<object?>(results);
            read = () => {
                var next = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return next is Exception error ? throw error : (IReadOnlyList<AcpiFirmwareTable>?)next;
            };
        }
        public AcpiReader(Func<IReadOnlyList<AcpiFirmwareTable>?> read) => this.read = read;
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() { Interlocked.Increment(ref count); return read(); }
    }

    private sealed class TopologyReader : IDeviceTopologyReader
    {
        private int count;
        public bool Fail { get; set; }
        public Func<DeviceTopologySnapshot>? Capture { get; set; }
        public int ReadCount => Volatile.Read(ref count);
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Interlocked.Increment(ref count);
            return Fail ? throw new InvalidOperationException("topology unavailable") : Capture?.Invoke() ?? DeviceInformationTestData.Snapshot();
        }
    }

    private sealed class StoreReader : IDeviceTopologySnapshotStore
    {
        public int SaveCount { get; private set; }
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token) { SaveCount++; return Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostManagerSamplingSubscriptionTestFixture sampling = new();
        public AcpiReader Acpi { get; }
        public TopologyReader Topology { get; } = new();
        public StoreReader Store { get; } = new();
        public DeviceTopologySnapshotProvider Provider { get; }
        public Fixture(AcpiReader reader)
        {
            Acpi = reader;
            Provider = new(Topology, Store, new(), sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance, reader);
        }
        public void Dispose() { Provider.Dispose(); sampling.Dispose(); }
    }
}
