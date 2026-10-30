using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class AdlxMetricsCollectorTests
{
    private const string RawMemoryName = "gpu_amd_adlx_dedicated_memory_mb";
    private static AmdAdlxReadRequest RawMemoryRequest() => Request(9, 3) with
        { IncludeUsage = false, IncludeVoltage = false, IncludeSampleTimestamp = false, IncludeMemory = true };

    [Theory]
    [InlineData(0, 64, true)] [InlineData(1, 64, true)] [InlineData(2147483647, 64, true)]
    [InlineData(-1, 64, false)] [InlineData(256, 0, false)]
    public void RawAdlxMemoryPreservesSdkIntegerWithoutByteConversion(int mb, int flags, bool available)
    {
        var row = Row(10, PnpA, flags); row.VramUsedMb = mb; row.TotalVramMb = 0;
        var gpu = Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(row, RawMemoryRequest(), Inventory()));
        Assert.Equal(available, gpu.AdditionalIntegerReadings.TryGetValue(MetricFieldCode.AdlxDedicatedMemoryMb, out var value));
        if (available) Assert.Equal((ulong)mb, value);
        var field = Assert.IsType<MetricFieldDefinition>(MetricFieldRegistry.FindByName(RawMemoryName));
        Assert.Equal("MB", field.Unit); Assert.Equal("unsigned64", field.ValueKind); Assert.False(field.HasLegacyAddress);
    }

    [Fact]
    public async Task RawMemoryDemandUsesOnlySelectedGpuAndMemoryFlag()
    {
        var row = Row(20, PnpB, 64); row.VramUsedMb = 321;
        var native = new Bridge { Metadata = [Row(10, PnpA), Row(20, PnpB)], Values = [row] };
        var request = RawMemoryRequest() with { DisplayIndexes = new HashSet<int> { 3 } };
        var gpu = Assert.Single(await new AmdAdlxBridgeReader(native).ReadAsync(request, Inventory(), CancellationToken.None));
        var selected = Assert.Single(native.Selections);
        Assert.Equal(20, selected.UniqueId); Assert.Equal(64, selected.RequestedFlags);
        Assert.Equal(321UL, gpu.AdditionalIntegerReadings[MetricFieldCode.AdlxDedicatedMemoryMb]);
    }

    [Fact]
    public void RawMemoryUsesOriginalWorkspaceUnsignedContractAndIndependentFailures()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"adlx-mb-{Guid.NewGuid():N}.bin"));
        output.WriteLine($"Native workspace resident bytes: {workspace.Capacity.ResidentByteCount}");
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "AMD A", "gpu-a", 0xA1, 1, 2, true), new(3, "AMD B", "gpu-b", 0xB1, 1, 2, true), new(7, "NVIDIA", "gpu-c", 0xC1, 1, 1, true)],
            [], [], workspace.CreateCatalogHandleMap());
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.FieldCode == MetricFieldCode.AdlxDedicatedMemoryMb).ToArray();
        Assert.Equal(2, bindings.Length); Assert.All(bindings, binding => Assert.Equal("amd.adlx", binding.SourceId));
        var request = WindowsHardwareMetricSampler.CreateAdlxReadRequest(bindings);
        Assert.Equal(64, request.NativeFlagsByDisplayIndex![9]); Assert.Equal(64, request.NativeFlagsByDisplayIndex[3]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        for (var phase = 0; phase < 5; phase++)
        {
            var at = now.AddSeconds(phase + 1); var plan = workspace.PlanCollection(bindings.Select(binding => binding.MetricHandle).ToArray(), modes, false, at);
            var source = Assert.Single(plan.Sources);
            var row = Row(10, PnpA, phase == 1 ? 0 : 64); row.VramUsedMb = phase == 2 ? -1 : 0;
            var other = Row(20, PnpB, 64); other.VramUsedMb = int.MaxValue;
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateAdlxCompletion(plan, source,
                new(source.SourceHandle, 1, (ulong)phase + 1), at, phase == 3 ? [] :
                [AmdAdlxBridgeReader.ToGpuMetrics(row, request, Inventory())!, AmdAdlxBridgeReader.ToGpuMetrics(other, request, Inventory())!]), at);
            var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
            foreach (var binding in bindings)
            {
                var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, binding.DeviceIdentity, new HashSet<string> { RawMemoryName })).Fields);
                var expected = phase == 3 || binding.DisplayIndex == 9 && phase is 1 or 2 ? null : binding.DisplayIndex == 9 ? "0" : "2147483647";
                Assert.Equal(expected is null ? null : MetricInteger.Parse(expected), field.IntegerValue); Assert.Null(field.NumericValue);
                Assert.Equal("MB", field.Unit); Assert.Equal("unsigned64", field.ValueKind);
                Assert.Equal(expected is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
            }
        }
    }

    [Fact]
    public async Task RawMemoryFreshHttpAndPushKeepSdkUnitsAndStopReadingWithoutDemand()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var root = Path.Combine(Path.GetTempPath(), $"adlx-mb-worker-{Guid.NewGuid():N}");
        var inventoryZone = new WindowsGpuAdapterOrderMonitoringZone(() => Inventory());
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider, new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete,
                RecordedGpuIdentities(Inventory()), NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])),
            new Environment(root), NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        using var workspaceHolding = owner.Hold(); var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire()) dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var device = dashboard.Current!.FieldBindings.Values.Single(binding => binding.Field.Code == MetricFieldCode.AdlxDedicatedMemoryMb && binding.DeviceIdentity == RecordedGpuIdentities(Inventory()).Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey).DeviceIdentity;
        var row = Row(10, PnpA, 64); row.VramUsedMb = 321;
        var native = new Bridge { Metadata = [Row(10, PnpA), Row(20, PnpB)], Values = [row] };
        var zone = new AmdAdlxMonitoringZone(new AmdAdlxBridgeReader(native));
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No system memory demand"), () => throw new InvalidOperationException("No system memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, inventoryZone,
            null!, null!, null!, null!, null!, zone, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, 
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, [RawMemoryName]);
        var streamCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHttpAsync(sampler, dashboard, streamCompleted);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + RawMemoryName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Null(ReadRawMemory(sampler.ReadLatest(request) ?? dashboard.Current!, device).IntegerValue);
        Assert.Equal(0, native.MetadataReads); await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("adlx-mb", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                // Require a successful sample before checking the selected native request.
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadRawMemory(stream.Current, device).IntegerValue != MetricInteger.Parse("321"));
                Assert.Equal(64, Assert.Single(native.Selections).RequestedFlags);
                var failed = row; failed.AvailableFlags = 0; Volatile.Write(ref native.Values, [failed]);
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadRawMemory(stream.Current, device).IntegerValue is not null);
                row.VramUsedMb = 0; Volatile.Write(ref native.Values, [row]);
                do { Assert.True(await stream.MoveNextAsync()); } while (ReadRawMemory(stream.Current, device).IntegerValue != MetricInteger.Parse("0"));
                using var pushCancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                    { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "adlx-mb-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, pushCancel.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var lines = new StreamReader(await response.Content.ReadAsStreamAsync(pushCancel.Token));
                using var pushed = JsonDocument.Parse((await lines.ReadLineAsync(pushCancel.Token))!);
                CheckRawMemoryWire(pushed.RootElement.GetProperty("value")[0].GetProperty("fields")[0]);
                await pushCancel.CancelAsync();
            }
            // Client cancellation is asynchronous across HTTP; wait for the endpoint to release its lease.
            await streamCompleted.Task.WaitAsync(timeout.Token);
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
                .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler)
                .GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token); gate.Release();
            var reads = Volatile.Read(ref native.SelectedReads);
            using var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token));
            CheckRawMemoryWire(fresh.RootElement[0].GetProperty("fields")[0]);
            Assert.True(Volatile.Read(ref native.SelectedReads) > reads);
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            await gate.WaitAsync(timeout.Token); gate.Release();
            var afterFetch = Volatile.Read(ref native.SelectedReads);
            await Task.Delay(150, timeout.Token); Assert.Equal(afterFetch, Volatile.Read(ref native.SelectedReads));
            await sampler.StopAsync(CancellationToken.None);
            Assert.Equal(MetricInteger.Parse("0"), ReadRawMemory(sampler.ReadLatest(request)!, device).IntegerValue);
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static DeviceMetricFieldValue ReadRawMemory(HardwareMetricSnapshot snapshot, string device)
        => Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, device, new HashSet<string> { RawMemoryName })).Fields);
    private static void CheckRawMemoryWire(JsonElement field)
    {
        Assert.Equal(RawMemoryName, field.GetProperty("name").GetString()); Assert.Equal("amd.adlx", field.GetProperty("sourceId").GetString());
        Assert.Equal("MB", field.GetProperty("unit").GetString()); Assert.Equal("unsigned64", field.GetProperty("valueKind").GetString());
        Assert.Equal("0", field.GetProperty("integerValue").GetString()); Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind);
    }
}
