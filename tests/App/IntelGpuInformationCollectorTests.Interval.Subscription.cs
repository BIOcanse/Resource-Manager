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
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    [Fact]
    public async Task IntervalSingleFetchAfterIdleReturnsTypedEmptyAndContinuousDemandRewarms()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture(); using var owner = CreateClockOwner(slots, secondHasDedicatedMemory: true);
        using var workspaceHolding = owner.Hold(); var dashboard = PublishClockCatalog(owner);
        var bindings = dashboard.Current!.FieldBindings.Values.Where(binding => IntervalCodes.Contains(binding.Field.Code)).ToArray();
        Assert.Equal(12, bindings.Length); Assert.All(bindings, binding => Assert.Equal("intel.igcl", binding.SourceId));
        var gpuIdentity = RecordedGpuIdentities().Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey;
        var device = bindings.Single(binding => binding.Field.Code == IntervalCodes[0] && binding.DeviceIdentity == gpuIdentity).DeviceIdentity;
        var sequence = 0; var badGpuEnergy = 0;
        var heldSdkTime = double.NaN;
        var sdk = new Sdk { SampleFactory = () =>
        {
            var telemetry = Sample(); var time = Interlocked.Increment(ref sequence) * 2.0 + 0.125;
            var heldTime = Volatile.Read(ref heldSdkTime);
            if (!double.IsNaN(heldTime)) time = heldTime;
            telemetry.Timestamp = Number(time, IntelGclUnit.Seconds); telemetry.GpuEnergy = Number(time * 3 + 0.125, IntelGclUnit.Joules);
            telemetry.MemoryEnergy = Number(time * 5 + 0.25, IntelGclUnit.Joules); telemetry.BoardEnergy = Number(time * 10 + 0.5, IntelGclUnit.Joules);
            telemetry.GlobalActivity = Number(time * 0.5 + 0.125, IntelGclUnit.Seconds);
            telemetry.RenderComputeActivity = Number(time * 0.25 + 0.125, IntelGclUnit.Seconds); telemetry.MediaActivity = Number(time * 0.125 + 0.125, IntelGclUnit.Seconds);
            if (Volatile.Read(ref badGpuEnergy) != 0) telemetry.GpuEnergy.Supported = 0;
            return telemetry;
        } };
        var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id => id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No memory demand"), () => throw new InvalidOperationException("No memory demand"));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, new WindowsGpuAdapterOrderMonitoringZone(Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelGclZone: zone);
        var names = IntervalNames.Append("gpu_intel_gpu_energy_joule").ToArray(); var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, names);
        Dictionary<string, DeviceMetricFieldValue> Fields(HardwareMetricSnapshot snapshot)
            => Assert.Single(MetricDeviceFields.Read(snapshot, device, names.ToHashSet())).Fields.ToDictionary(field => field.Name);
        await using var app = await StartIntelHttpAsync(sampler, dashboard);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var query = "?device=" + Uri.EscapeDataString(device) + "&fields=" + string.Join(',', IntervalNames);
        Assert.All(Fields(sampler.ReadLatest(request) ?? dashboard.Current!).Values, field => Assert.Null(field.NumericValue));
        Assert.Equal(0, sdk.Opens); await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("igcl-interval", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[IntervalNames[0]].NumericValue != 3);
                for (var i = 0; i < IntervalNames.Length; i++) Assert.Equal(IntervalValues[i], Fields(stream.Current)[IntervalNames[i]].NumericValue);
                Assert.NotNull(Fields(stream.Current)["gpu_intel_gpu_energy_joule"].NumericValue);
                using (var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token))) CheckIntervalWire(wire.RootElement[0].GetProperty("fields"));
                using var cancelPush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var push = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "igcl-interval-wire", path = "/api/metrics/subscribe" + query } } }) };
                using var response = await client.SendAsync(push, HttpCompletionOption.ResponseHeadersRead, cancelPush.Token); response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancelPush.Token));
                using var pushed = JsonDocument.Parse((await reader.ReadLineAsync(cancelPush.Token))!); CheckIntervalWire(pushed.RootElement.GetProperty("value")[0].GetProperty("fields"));
                await cancelPush.CancelAsync();
                Volatile.Write(ref sdk.Mode, 1);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[IntervalNames[0]].NumericValue is not null);
                Assert.All(IntervalNames, name => Assert.Null(Fields(stream.Current)[name].NumericValue));
                Volatile.Write(ref sdk.Mode, 0);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[IntervalNames[0]].NumericValue != 3);
                Volatile.Write(ref badGpuEnergy, 1);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[IntervalNames[0]].NumericValue is not null);
                Assert.Equal(5, Fields(stream.Current)[IntervalNames[1]].NumericValue); Assert.Equal(10, Fields(stream.Current)[IntervalNames[2]].NumericValue);
                Assert.Null(Fields(stream.Current)["gpu_intel_gpu_energy_joule"].NumericValue);
                Volatile.Write(ref badGpuEnergy, 0);
                do { Assert.True(await stream.MoveNextAsync()); } while (Fields(stream.Current)[IntervalNames[0]].NumericValue != 3);
            }
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token); gate.Release(); var calls = Volatile.Read(ref sdk.TelemetryCalls);
            await WaitForEmptyCounterDemandAsync(sampler, owner, timeout.Token);
            // Keep even a later delivery from acquiring a second SDK interval.
            // Baseline cleanup is acknowledged above, not caused by this clock.
            Volatile.Write(ref heldSdkTime, (Volatile.Read(ref sequence) + 1) * 2.0 + 0.125);
            var fetchStarted = DateTimeOffset.UtcNow;
            using (var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query
                + ",gpu_intel_gpu_energy_joule", timeout.Token)))
                CheckColdIntervalWire(fresh.RootElement[0].GetProperty("fields"),
                    Volatile.Read(ref heldSdkTime) * 3 + 0.125, fetchStarted);
            Assert.True(Volatile.Read(ref sdk.TelemetryCalls) > calls);
            var cold = sampler.ReadLatest(request)!;
            Assert.All(request.Ids, id => Assert.True(cold.Datasets[id].LastAttemptAtUtcTicks >= fetchStarted.UtcTicks));
            Assert.All(IntervalNames, name => Assert.Null(Fields(cold)[name].NumericValue));
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            await WaitForEmptyCounterDemandAsync(sampler, owner, timeout.Token);

            // Keep demand while the SDK advances to verify a complete interval.
            // A one-shot response must not silently wait for a numeric value.
            Volatile.Write(ref heldSdkTime, double.NaN);
            await using (var resumed = sampler.SubscribeAsync("igcl-interval-after-idle", request,
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                do { Assert.True(await resumed.MoveNextAsync()); }
                while (Fields(resumed.Current)[IntervalNames[0]].NumericValue != IntervalValues[0]);
                for (var i = 0; i < IntervalNames.Length; i++)
                {
                    var field = Fields(resumed.Current)[IntervalNames[i]];
                    Assert.Equal(IntervalValues[i], field.NumericValue);
                    Assert.Equal(SamplingObservationStatus.Current, field.Status);
                    Assert.Equal(2_000UL, field.SampleDurationMilliseconds);
                }
                using var wire = JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot" + query, timeout.Token));
                CheckIntervalWire(wire.RootElement[0].GetProperty("fields"));
            }
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            await gate.WaitAsync(timeout.Token); gate.Release();
            await WaitForEmptyCounterDemandAsync(sampler, owner, timeout.Token);
            var afterFetch = Volatile.Read(ref sdk.TelemetryCalls);
            await Task.Delay(200, timeout.Token); Assert.Equal(afterFetch, Volatile.Read(ref sdk.TelemetryCalls));
            Assert.Equal(0, sdk.PciCalls); Assert.Equal(0, sdk.PciPropertiesCalls);
            lock (sdk.TelemetryDevices) Assert.All(sdk.TelemetryDevices, handle => Assert.Equal(new IntPtr(1), handle));
            await sampler.StopAsync(CancellationToken.None); Assert.Equal(sdk.Opens, sdk.Closes);
            for (var i = 0; i < IntervalNames.Length; i++) Assert.Equal(IntervalValues[i], Fields(sampler.ReadLatest(request)!)[IntervalNames[i]].NumericValue);
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitForEmptyCounterDemandAsync(WindowsHardwareMetricSampler sampler,
        HostManagerMetricSnapshotOwner owner, CancellationToken cancellationToken)
    {
        var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler)
            .GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        var signal = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler)
            .GetField("demandSignal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var lease = owner.Acquire();
            // An already-created plan could predate the last lease removal.
            // Two sequential worker acknowledgments rule that plan out.
            for (var round = 0; round < 2; round++)
            {
                var before = lease.Workspace.ReadHeader();
                ResourceManager.Subscription.NativeSamplingSubscriptionWorker<MetricSampleRequest, SamplingOwnerToken>
                    .SignalDemand(signal);
                var after = lease.Workspace.ReadHeader();
                while (after.LastOperationEpoch <= before.LastOperationEpoch)
                {
                    await Task.Delay(1, cancellationToken);
                    after = lease.Workspace.ReadHeader();
                }
                Assert.Equal(before.CommittedGeneration, after.CommittedGeneration);
                Assert.Equal(before.LastPlanEpoch, after.LastPlanEpoch);
            }
        }
        finally { gate.Release(); }
    }

    private static void CheckColdIntervalWire(JsonElement fields, double energy, DateTimeOffset fetchStarted)
    {
        Assert.Equal(7, fields.GetArrayLength());
        var rows = fields.EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        for (var i = 0; i < IntervalNames.Length; i++)
        {
            var row = rows[IntervalNames[i]];
            Assert.Equal("intel.igcl", row.GetProperty("sourceId").GetString());
            Assert.Equal(i < 3 ? "W" : "%", row.GetProperty("unit").GetString());
            Assert.Equal("float64", row.GetProperty("valueKind").GetString());
            Assert.Equal((int)SamplingObservationStatus.Unavailable, row.GetProperty("status").GetInt32());
            foreach (var property in new[] { "numericValue", "integerValue", "booleanValue", "bytesValue",
                "observedAt", "sampleDurationMilliseconds" })
                Assert.Equal(JsonValueKind.Null, row.GetProperty(property).ValueKind);
        }
        var raw = rows["gpu_intel_gpu_energy_joule"];
        Assert.Equal("intel.igcl", raw.GetProperty("sourceId").GetString());
        Assert.Equal("J", raw.GetProperty("unit").GetString());
        Assert.Equal("float64", raw.GetProperty("valueKind").GetString());
        Assert.Equal((int)SamplingObservationStatus.Current, raw.GetProperty("status").GetInt32());
        Assert.Equal(energy, raw.GetProperty("numericValue").GetDouble());
        // The original native observation timestamp has millisecond precision.
        Assert.True(raw.GetProperty("observedAt").GetDateTimeOffset()
            >= DateTimeOffset.FromUnixTimeMilliseconds(fetchStarted.ToUnixTimeMilliseconds()));
    }

    private static void CheckIntervalWire(JsonElement fields)
    {
        Assert.Equal(6, fields.GetArrayLength()); var rows = fields.EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!);
        for (var i = 0; i < IntervalNames.Length; i++)
        {
            var row = rows[IntervalNames[i]]; Assert.Equal("intel.igcl", row.GetProperty("sourceId").GetString());
            Assert.Equal(i < 3 ? "W" : "%", row.GetProperty("unit").GetString()); Assert.Equal("float64", row.GetProperty("valueKind").GetString());
            Assert.Equal(IntervalValues[i], row.GetProperty("numericValue").GetDouble()); Assert.Equal(JsonValueKind.Null, row.GetProperty("integerValue").ValueKind);
        }
    }
}
