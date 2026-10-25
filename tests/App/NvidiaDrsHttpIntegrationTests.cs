using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Net;
using ResourceManager.Subscription.Native;
using Xunit.Abstractions;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.Subscription;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsHttpIntegrationTests(ITestOutputHelper output)
{
    private static readonly NvidiaDrsReadLimits Limits = new(2, 4, 4, 1, 100_000);
    private static object Request(string name = "Game") => new
    { profiles = new[] { new { profileName = name, settingIds = new uint[] { 7 } } }, limits = Limits };

    [Fact]
    public async Task RealRoutesPreserveQwordCurrentAndOriginalAnonymousOriginPolicy()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        foreach (var action in new[] { "current", "read" })
        {
            using var cross = new HttpRequestMessage(HttpMethod.Post, Route(action)) { Content = JsonContent.Create(Request()) };
            cross.Headers.Add("Origin", "https://other.example");
            using var rejected = await fixture.Client.SendAsync(cross, deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var wrong = new HttpRequestMessage(HttpMethod.Post, Route(action)) { Content = JsonContent.Create(Request()) };
            wrong.Headers.Host = "other.example";
            using var wrongResponse = await fixture.Client.SendAsync(wrong, deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, wrongResponse.StatusCode);
        }
        Assert.Equal(0, fixture.Recording.Creates);
        using (var passive = await fixture.PostAsync("current", Request(), deadline.Token))
        {
            Assert.Equal(HttpStatusCode.OK, passive.StatusCode);
            var values = await passive.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Assert.Equal(JsonValueKind.Null, values[0].GetProperty("value").ValueKind);
        }
        Assert.Equal(0, fixture.Recording.Creates);
        using var read = await fixture.PostAsync("read", Request(), deadline.Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var sampled = await read.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        AssertQword(sampled);
        using var current = await fixture.PostAsync("current", Request(), deadline.Token);
        var cached = await current.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(sampled.GetRawText(), cached.GetRawText());
        Assert.Equal(1, fixture.Recording.Creates);
        using var original = await fixture.Client.GetAsync("/api/device-topology/fields", deadline.Token);
        var originalJson = await original.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Contains(originalJson.GetProperty("devices").EnumerateArray(), row =>
            row.GetProperty("deviceIdentity").GetString() == NvidiaDrsInformationRequest.Identity("Game", 7));
        using var discovered = await fixture.Client.PostAsJsonAsync(Route("discover"), Limits, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
        Assert.Equal(2, fixture.Recording.Creates); Assert.Equal(2, fixture.Recording.Destroys);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Theory]
    [InlineData("Game\\uD800")]
    [InlineData("Game\\uDC00")]
    public async Task RealHttpRejectsEscapedUnpairedSurrogatesBeforeSource(string escapedName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        var json = JsonSerializer.Serialize(Request()).Replace("\"Game\"", "\"" + escapedName + "\"", StringComparison.Ordinal);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(Route("read"), content, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, fixture.Recording.Creates);
    }

    [Theory]
    [InlineData("Game\\uD83D\\uDE00", "Game\U0001F600")]
    [InlineData("Game\\uFFFD", "Game\uFFFD")]
    public async Task RealHttpPreservesLegalPairAndReplacementAsDistinctNames(string escaped, string exact)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        fixture.Recording.ProfileName = exact;
        var json = JsonSerializer.Serialize(Request()).Replace("\"Game\"", "\"" + escaped + "\"", StringComparison.Ordinal);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(Route("read"), content, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(exact, Packet(values).GetProperty("profileName").GetString());
        Assert.Equal(1, fixture.Recording.Creates);
    }

    [Fact]
    public async Task UnifiedSelectorUsesOriginalWorkerAndStopPreservesCurrentAndDirectStillWorks()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var path = NvidiaDrsSubscriptionPath.Create(new("Game", new uint[] { 7 }), Limits, TimeSpan.FromMilliseconds(100));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "drs", path } } }) };
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, streamCancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var frame = await ReadObservedFrameAsync(stream, deadline.Token);
        Assert.Equal("drs", frame.GetProperty("subscriptionId").GetString());
        AssertQword(frame.GetProperty("value"));
        Assert.True(fixture.Recording.Reads > 0);
        await streamCancellation.CancelAsync(); response.Dispose();
        await fixture.Provider.StopAsync(deadline.Token);
        var before = fixture.Provider.ReadState(); var reads = fixture.Recording.Reads;
        using var current = await fixture.PostAsync("current", Request(), deadline.Token);
        AssertQword(await current.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
        Assert.Same(before, fixture.Provider.ReadState()); Assert.Equal(reads, fixture.Recording.Reads);
        using var direct = await fixture.PostAsync("read", Request(), deadline.Token);
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode); Assert.Equal(reads + 1, fixture.Recording.Reads);
    }

    [Fact]
    public async Task FreezeDuringNativeReadRejectsReadAndDirectoryCommitAndLaterReadRecovers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        fixture.Recording.OnSetting = () => fixture.Zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        var before = fixture.Provider.ReadState();
        using var rejected = await fixture.PostAsync("read", Request(), deadline.Token);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode); Assert.Same(before, fixture.Provider.ReadState());
        fixture.Zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        using var directory = await fixture.Client.PostAsJsonAsync(Route("discover"), Limits, deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, directory.StatusCode); Assert.Same(before, fixture.Provider.ReadState());
        fixture.Recording.OnSetting = null; fixture.Zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        using var recovered = await fixture.PostAsync("read", Request(), deadline.Token);
        AssertQword(await recovered.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
    }

    [Fact]
    public async Task SettingFailureClearsSelectedHttpCurrentAndRecoveryDoesNotKeepLastGoodValue()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        using var good = await fixture.PostAsync("read", Request(), deadline.Token);
        AssertQword(await good.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
        fixture.Recording.FailSetting = true;
        using var failed = await fixture.PostAsync("read", Request(), deadline.Token);
        var values = await failed.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(JsonValueKind.Null, values[0].GetProperty("value").ValueKind);
        using var current = await fixture.PostAsync("current", Request(), deadline.Token);
        var cached = await current.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(JsonValueKind.Null, cached[0].GetProperty("value").ValueKind);
        fixture.Recording.FailSetting = false;
        using var recovered = await fixture.PostAsync("read", Request(), deadline.Token);
        AssertQword(await recovered.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
    }

    [Fact]
    public async Task ObserveOriginalNet10HttpBinderSeparatelyFromTheDrsCandidateConverter()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        foreach (var escaped in new[] { "\\uD800", "\\uDC00", "\\uD83D\\uDE00", "\\uFFFD" })
        {
            using var body = new StringContent("{\"profileName\":\"" + escaped + "\"}", Encoding.UTF8, "application/json");
            using var response = await fixture.Client.PostAsync("/api/recording/drs-default-binder", body, deadline.Token);
            var text = await response.Content.ReadAsStringAsync(deadline.Token);
            // Diagnostic observation, not a claimed default-binder rejection assertion. Main uses the actual
            // status/body to decide whether the candidate production converter is necessary before acceptance.
            output.WriteLine("DEFAULT_NET10_BINDER {0} => {1}: {2}", escaped, (int)response.StatusCode, text);
            if (escaped is "\\uD83D\\uDE00" or "\\uFFFD") Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(0, fixture.Recording.Creates);
    }

    [Fact]
    public async Task OverlappingUnifiedSubscriptionsShareOneOriginalLogicalItem()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        using var channel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var path = NvidiaDrsSubscriptionPath.Create(new("Game", new uint[] { 7 }), Limits, TimeSpan.FromMilliseconds(100));
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "a", path }, new { id = "b", path } } }) };
        using var response = await fixture.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, channel.Token);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var seen = new HashSet<string>();
        for (var round = 0; round < 16 && seen.Count != 2; round++)
        {
            var frame = await ReadObservedFrameAsync(stream, deadline.Token);
            seen.Add(frame.GetProperty("subscriptionId").GetString()!); AssertQword(frame.GetProperty("value"));
        }
        Assert.Equal(2, seen.Count);
        var snapshot = fixture.Sampling.Owner.Execute(2, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader { AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()) };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (header.PersistentSourceCount, header.ItemOutputCount);
        });
        Assert.Equal(2U, snapshot.PersistentSourceCount); Assert.Equal(1U, snapshot.ItemOutputCount);
        await channel.CancelAsync(); response.Dispose(); await fixture.Provider.StopAsync(deadline.Token);
    }

    private static async Task<JsonElement> ReadObservedFrameAsync(StreamReader stream, CancellationToken token)
    {
        for (var round = 0; round < 32; round++)
        {
            var line = await stream.ReadLineAsync(token) ?? throw new EndOfStreamException();
            using var document = JsonDocument.Parse(line);
            var values = document.RootElement.GetProperty("value");
            if (values.GetArrayLength() == 1 && values[0].GetProperty("value").ValueKind == JsonValueKind.Object)
                return document.RootElement.Clone();
        }
        throw new InvalidDataException("No actual DRS field observation reached the unified channel.");
    }

    [Fact]
    public async Task RealHostGlobalCatalogCannotBindDrsToOrdinarySnapshotCollection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            fixture.Provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        using var read = await fixture.PostAsync("read", Request(), deadline.Token);
        AssertQword(await read.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
        var field = new FieldReference(NvidiaDrsInformationRequest.Identity("Game", 7), "gpu_nvidia_driver_profile_setting_value");
        Assert.NotNull(fixture.Provider.ReadState().InformationFields.FindField(field));
        Assert.DoesNotContain(host.Reads.Catalog.Fields, capability => capability.Field == field);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Reads.CollectAsync([field], deadline.Token).AsTask());
        Assert.Throws<ArgumentException>(() => host.Subscriptions.Acquire("wrong-snapshot", [field], TimeSpan.FromMilliseconds(100)));
        _ = await fixture.Provider.CaptureInformationAsync(deadline.Token);
        Assert.Equal(1, fixture.Recording.Reads);
        Assert.NotNull(fixture.Provider.ReadState().InformationFields.FindField(field)!.Value);
        Assert.DoesNotContain(host.Reads.Catalog.Fields, capability => capability.Field == field);
    }

    [Fact]
    public async Task DisposeDuringBlockedOriginalWorkerSettlesPlanBeforeClearingAndClosingCapture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Recording.OnSetting = () => { entered.TrySetResult(true); release.Wait(deadline.Token); };
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7 })], Limits);
        var oldAccess = fixture.Provider.CreateNvidiaDrsInformation(scope);
        await using var stream = fixture.Provider.SubscribeNvidiaDrsFieldsAsync("blocked-dispose", scope,
            TimeSpan.FromMilliseconds(100), ResourceManager.Subscription.FieldSubscriptionOptions.Default, deadline.Token)
            .GetAsyncEnumerator(deadline.Token);
        var pending = stream.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var before = fixture.Provider.ReadState();
        var direct = fixture.Provider.ReadNvidiaDrsInformationAsync(scope, deadline.Token).AsTask();
        var disposal = Task.Run(fixture.Provider.Dispose);
        try
        {
            while (true)
            {
                try { _ = fixture.Provider.CreateNvidiaDrsInformation(scope); }
                catch (ObjectDisposedException) { break; }
                await Task.Delay(1, deadline.Token);
            }
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, fixture.Recording.Destroys);
        }
        finally { release.Set(); }
        await disposal.WaitAsync(deadline.Token);
        try { _ = await direct.WaitAsync(deadline.Token); }
        catch (ObjectDisposedException) { }
        _ = await pending.WaitAsync(deadline.Token);
        while (true)
        {
            var cleared = fixture.Sampling.Owner.Execute(2, session =>
            {
                var header = new NativeSamplingSubscriptionSnapshotHeader { AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()) };
                var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
                var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
                Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
                return header.PersistentSourceCount == 0 && header.ItemOutputCount == 0;
            });
            if (cleared)
            {
                try { _ = await oldAccess.Backend.CollectAsync(scope.Fields, deadline.Token); }
                catch (ObjectDisposedException) { break; }
            }
            await Task.Delay(10, deadline.Token);
        }
        Assert.Same(before, fixture.Provider.ReadState());
        Assert.Equal(1, fixture.Recording.Reads);
        Assert.Equal(1, fixture.Recording.Destroys);
    }

    [Fact]
    public async Task HostFreshFrameCapturesAcceptedValueBeforeCompetingDirectChangesCurrent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.StartAsync(deadline.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7 })], Limits);
        var access = fixture.Provider.CreateNvidiaDrsInformation(scope);
        var blockNextRead = 0;
        var backend = new InformationReadWriteBackend(() =>
        {
            if (Interlocked.Exchange(ref blockNextRead, 0) != 0)
            {
                entered.TrySetResult(true);
                release.Wait(deadline.Token);
            }
            return WindowsInformationFieldProjection.NvidiaDrsCurrent(fixture.Provider.ReadState().InformationFields, scope);
        }, _ => throw new InvalidOperationException("The original worker must capture this typed DRS request."),
            static _ => new(true, null, []));
        fixture.Recording.Value = 1;
        fixture.Recording.OnSetting = () => Volatile.Write(ref blockNextRead, 1);
        await using var stream = fixture.Provider.SubscribeFieldsAsync("host-drs-fresh-commit", scope.Fields,
            TimeSpan.FromMilliseconds(5000), new(FieldSubscriptionSamplingMode.FreshOnTrigger), backend,
            DeviceTopologyFieldScope.NvidiaDrs, deadline.Token, scope).GetAsyncEnumerator(deadline.Token);
        var first = stream.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        fixture.Recording.OnSetting = null;
        fixture.Recording.Value = 2;
        var directStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = Task.Run(async () =>
        {
            directStarted.SetResult(true);
            return await fixture.Provider.ReadNvidiaDrsInformationAsync(scope, deadline.Token);
        }, deadline.Token);
        try
        {
            await directStarted.Task.WaitAsync(deadline.Token);
            await Task.Delay(50, deadline.Token);
            Assert.False(direct.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref fixture.Recording.Reads));
        }
        finally { release.Set(); }
        Assert.True(await first.WaitAsync(deadline.Token));
        var fresh = Assert.Single(stream.Current);
        Assert.Equal(FieldObservationStatus.Current, fresh.Status);
        Assert.Equal("1", fresh.Value!.Structure!.Value.GetProperty("currentValue").GetProperty("value").GetString());
        _ = await direct.WaitAsync(deadline.Token);
        Assert.Equal(2, Volatile.Read(ref fixture.Recording.Reads));
        Assert.Equal("2", access.Backend.ReadCached(scope.Fields[0]).Value!.Structure!.Value
            .GetProperty("currentValue").GetProperty("value").GetString());
        Assert.Equal("1", fresh.Value.Structure!.Value.GetProperty("currentValue").GetProperty("value").GetString());
        await fixture.Provider.StopAsync(deadline.Token);
    }

    private static string Route(string action) => "/api/device-topology/nvidia-drs/fields/" + action;
    private static JsonElement Packet(JsonElement values) => values[0].GetProperty("value").GetProperty("structure");
    private static void AssertQword(JsonElement values) => Assert.Equal("18446744073709551615",
        Packet(values).GetProperty("currentValue").GetProperty("value").GetString());

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly HostManagerSamplingSubscriptionTestFixture Sampling = new();
        internal readonly NvidiaDrsHostRecording Recording = new();
        internal readonly Administrator Administrator = new();
        internal NvidiaNvapiMonitoringZone Zone { get; }
        internal DeviceTopologySnapshotProvider Provider { get; }
        private WebApplication? app;
        internal HttpClient Client { get; private set; } = null!;
        private Fixture()
        {
            Zone = new(new NvidiaNvapiReader(Recording));
            Provider = new(new EmptyReader(), new Store(), new(), Sampling.Owner,
                NullLogger<DeviceTopologySnapshotProvider>.Instance, new EmptyAcpi(), nvidiaDrsZone: Zone);
        }
        internal static async Task<Fixture> StartAsync(CancellationToken token)
        {
            var fixture = new Fixture();
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
                builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddSingleton(fixture.Provider);
                builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(fixture.Provider);
                builder.Services.AddHostedService(_ => fixture.Provider);
                builder.Services.AddSingleton(new LoopbackApiAccessToken(_ => false, (_, _) => false));
                builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(fixture.Administrator);
                fixture.app = builder.Build();
                fixture.app.UseRouting(); fixture.app.UseMiddleware<LoopbackApiAuthenticationMiddleware>();
                fixture.app.MapPost("/api/recording/drs-default-binder", (PlainDrsName request) => Results.Ok(request)).AllowAnonymous();
                foreach (var method in new[] { "MapDeviceTopologyEndpoints", "MapSubscriptionChannelEndpoints" })
                    typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [fixture.app]);
                await fixture.app.StartAsync(token);
                fixture.Client = new HttpClient { BaseAddress = new Uri(Assert.Single(fixture.app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses)), Timeout = Timeout.InfiniteTimeSpan };
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<HttpResponseMessage> PostAsync(string action, object request, CancellationToken token)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Route(action)) { Content = JsonContent.Create(request) };
            message.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            return await Client.SendAsync(message, token);
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (app is not null) { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)); await app.DisposeAsync(); }
            Provider.Dispose(); Zone.Dispose(); Sampling.Dispose();
        }
    }
    private sealed record PlainDrsName(string ProfileName);
    private sealed class Administrator : ILoopbackApiAdministratorReader
    {
        internal int Calls;
        public bool IsAdministrator(HttpContext context)
        { Calls++; throw new InvalidOperationException("Anonymous read must not attest an administrator."); }
    }
    private sealed class EmptyAcpi : IAcpiFirmwareTableReader { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }
    private sealed class EmptyReader : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => throw new InvalidOperationException("No ordinary topology was selected."); }
    private sealed class Store : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token)
            => throw new InvalidOperationException("No disk journal was selected.");
    }
}
