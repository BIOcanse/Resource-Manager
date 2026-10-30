using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

// Reuses the real Host HTTP routes and unified-channel helpers of the fan HTTP tests.
public sealed partial class NvidiaFanHttpIntegrationTests
{
    [Fact]
    public async Task GpuEngineOnlyFieldsSubscriptionEmitsCurrentFramesFromThePdhRead()
    {
        // Real machine: a dashboard bound only to gpu_all_engine_busy_percent sent
        // /api/metrics/subscribe?fields=gpu_all_engine_busy_percent and the channel emitted no frame,
        // while /api/metrics/fields/snapshot reported every engine NotRequested.
        const ulong luid = 0xE4A6;
        const string field = "gpu_all_engine_busy_percent";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The live GPU check uses the topology owner's physical identity projection, so the recorded
        // catalog is derived from the same inventory instead of a hand-written identity.
        const string pnp = @"PCI\VEN_1002&DEV_164E&SUBSYS_12345678&REV_D8\4&1a2b3c4d&0&0041";
        var inventoryRead = new WindowsGpuAdapterInventoryRead(
            SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
            [new WindowsGpuAdapter(0, "Recorded GPU", 0x1002, 0x164E, 0, new AdapterLuid { LowPart = (uint)luid }, false, 0,
                WindowsGpuAdapterKind.Integrated)
            {
                PhysicalIdentityStatus = SamplingObservationStatus.Current,
                PhysicalIdentities = [new(0, pnp, WindowsGpuPnpKind.Pci, true, false, 0, 0, 0, 0, 0x1002, 0x164E, 0, 0, 1,
                    WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode |
                    WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip | WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress,
                    WindowsGpuPhysicalIdentityStatus.Bound, 0)]
            }]);
        var captured = ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsHardwareMetricTopologySource.CaptureGpuIdentities(inventoryRead);
        Assert.Null(captured.FailureCode);
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]) with
        {
            GpuAdapters = captured.Rows
        });
        var inventory = new WindowsGpuAdapterOrderMonitoringZone(() => inventoryRead);
        var pdh = new RecordedGpuEngineSource(luid);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, inventory, null!,
            new PdhGpuEngineMonitoringZone(pdh), null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!,
            fixture.Owner, fixture.Catalog,  fixture.Slots.Owner, fixture.Slots.Provider,
            NullLogger<WindowsHardwareMetricSampler>.Instance,
            borrowedControls: new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null,
                ControlReadCpuTopology = static () => new CpuOsTopologyInformation(null, null) });

        // The catalog probe discovers one public field per PDH engine of the adapter.
        await sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, timeout.Token);
        var engines = fixture.Catalog.Current!.FieldBindings.Values
            .Where(static binding => binding.PdhGpuEngineIdentity is not null).ToArray();
        Assert.Equal(new[] { "gpu-engine|000000000000E4A6|0|0|0", "gpu-engine|000000000000E4A6|0|1|2" },
            engines.Select(static binding => binding.DeviceIdentity).Order(StringComparer.Ordinal).ToArray());

        using var dependencyHttp = new HttpClient();
        var controls = new UnusedControls();
        var core = new ReadWriteCore(new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls, controls),
            new DependencyActions(dependencyHttp, []));
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHttpAsync(sampler, fixture.Catalog, fixture.Slots.Provider, core, ended, timeout.Token);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)), Timeout = Timeout.InfiniteTimeSpan };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        await sampler.StartAsync(timeout.Token);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "engine", path = $"/api/metrics/subscribe?fields={field}&intervalMs=100" } } }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            // The engine-only subscription must publish frames, and they carry the PDH engine values as Current.
            var frame = await ReadUntilAsync(reader, static value => EngineValues(value).Count == 2
                && EngineValues(value).Values.All(static item => item.Status == (int)SamplingObservationStatus.Current),
                timeout.Token, "engine");
            AssertEngineValues(EngineValues(frame));

            using (var cached = await GetJsonAsync(client, $"/api/metrics/fields/snapshot?fields={field}", timeout.Token))
                AssertEngineValues(EngineValues(cached.RootElement));
            Assert.True(pdh.Reads > 0);

            await cancellation.CancelAsync(); response.Dispose(); reader.Dispose();
            await ended.Task.WaitAsync(timeout.Token);
        }
        finally
        { await sampler.StopAsync(CancellationToken.None); await app.StopAsync(CancellationToken.None); }

        static void AssertEngineValues(IReadOnlyDictionary<string, (int Status, double? Value)> values)
        {
            Assert.Equal((int)SamplingObservationStatus.Current, values["gpu-engine|000000000000E4A6|0|0|0"].Status);
            Assert.Equal(42.5, values["gpu-engine|000000000000E4A6|0|0|0"].Value);
            Assert.Equal((int)SamplingObservationStatus.Current, values["gpu-engine|000000000000E4A6|0|1|2"].Status);
            Assert.Equal(7, values["gpu-engine|000000000000E4A6|0|1|2"].Value);
        }

        static Dictionary<string, (int Status, double? Value)> EngineValues(JsonElement json)
            => json.EnumerateArray().ToDictionary(
                static device => device.GetProperty("deviceIdentity").GetString()!,
                static device =>
                {
                    var item = Assert.Single(device.GetProperty("fields").EnumerateArray());
                    var value = item.GetProperty("numericValue");
                    return (item.GetProperty("status").GetInt32(),
                        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : (double?)null);
                }, StringComparer.Ordinal);
    }

    // PDH engine rows for one adapter: engine 0 is busy in two processes (30 + 12.5), engine 1 (Compute) in one.
    private sealed class RecordedGpuEngineSource(ulong luid) : INativePdhSnapshotSource
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);

        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
        {
            var sequence = (ulong)Interlocked.Increment(ref reads);
            NativePdhGpuEngineRow[] rows =
            [
                new() { AdapterLuid = luid, ProcessId = 100, EngineClass = NativePdhEngineClass.Other, PhysicalIndex = 0, EngineIndex = 0, UsagePercent = 30 },
                new() { AdapterLuid = luid, ProcessId = 200, EngineClass = NativePdhEngineClass.Other, PhysicalIndex = 0, EngineIndex = 0, UsagePercent = 12.5 },
                new() { AdapterLuid = luid, ProcessId = 100, EngineClass = NativePdhEngineClass.Compute, PhysicalIndex = 0, EngineIndex = 1, UsagePercent = 7 }
            ];
            return new(new NativePdhSnapshot(DateTimeOffset.UtcNow, sequence, 1, rows, [], default),
                NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0);
        }
    }
}
