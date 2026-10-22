using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class NpuMemoryHostServiceGraphTests
{
    [Fact]
    public void ProductionNormalProfileRegistersOnePassiveSingletonNpuMemoryZone()
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        var descriptor = Assert.Single(registrations, row => row.ServiceType == typeof(WindowsNpuMemoryMonitoringZone));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var services = registrations.BuildServiceProvider();
        Assert.Same(services.GetRequiredService<WindowsNpuMemoryMonitoringZone>(),
            services.GetRequiredService<WindowsNpuMemoryMonitoringZone>());
        // No hosted service is started and no directory/read is invoked.
    }
}

// Reuse the existing real HTTP routes, controls guard and Host server lifecycle.
public sealed partial class NvidiaFanHttpIntegrationTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(18446744073709551615UL)]
    public async Task NormalNpuHttpUsesExactTypedIdentityOriginalHostCurrentAndUnsignedBytes(ulong resident)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var recording = new NpuMemoryIntegrationRecording { SharedResidentBytes = resident };
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(recording.Topology),
            new TestEnvironment(Path.Combine(AppContext.BaseDirectory, "npu-memory-host", Guid.NewGuid().ToString("N"))),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(timeout.Token);
        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        var zone = new WindowsNpuMemoryMonitoringZone(recording.Read);
        var registry = new MonitoringSourceZoneRegistry([new WindowsMemoryMonitoringZone(), zone]);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, npuMemoryZone: zone);
        using var dependencyHttp = new HttpClient();
        var controls = new UnusedControls();
        var backend = new HostReadWriteBackend(sampler, sampler, dashboard, controls, controls);
        var core = new ReadWriteCore(backend, new DependencyActions(dependencyHttp, []));
        var identity = NpuMemoryIntegrationRecording.Identity;
        var name = NpuMemoryIntegrationRecording.SharedResident;
        var reference = new FieldReference(identity, name);
        Assert.Equal("npu", core.Catalog.Devices.Single(device => device.Identity == identity).Resource);
        const string busyName = "npu_all_engine_busy_percent";
        var expectedMemoryFields = new[]
        {
            "npu_all_dedicated_memory_committed",
            "npu_all_dedicated_memory_resident",
            "npu_all_shared_memory_committed",
            NpuMemoryIntegrationRecording.SharedResident
        };
        var identityFields = core.Catalog.Fields.Where(field => field.Field.DeviceIdentity == identity)
            .Select(field => field.Field.FieldName).ToArray();
        Assert.Equal(expectedMemoryFields.OrderBy(static field => field, StringComparer.Ordinal),
            identityFields.Where(field => field != busyName).OrderBy(static field => field, StringComparer.Ordinal));
        Assert.Equal(expectedMemoryFields.Length + 1, identityFields.Length);
        var busyField = Assert.Single(core.Catalog.Fields, field =>
            field.Field.DeviceIdentity == identity && field.Field.FieldName == busyName).Field;
        var busyCapability = core.Query(busyField);
        Assert.True(busyCapability.Read.Supported);
        Assert.Equal("windows.npu-engine", busyCapability.ReadSource);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, identity, [name]);
        _ = await sampler.GetSnapshotAsync(request, timeout.Token);
        Assert.Equal(0, recording.Reads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("npu-memory-original-worker", request,
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync());
                var delivered = NpuMemoryCaptureIntegrationTests.Npu(stream.Current, name);
                Assert.Equal(resident.ToString(System.Globalization.CultureInfo.InvariantCulture), delivered.IntegerValue);
                Assert.Null(delivered.NumericValue);
                Assert.Equal(SamplingObservationStatus.Current, delivered.Status);
                Assert.Equal("windows.npu-memory", delivered.SourceId);
            }
            await sampler.StopAsync(timeout.Token);
            Assert.True(recording.Reads > 0);
            var count = recording.Reads;
            var current = sampler.ReadLatest(request);
            Assert.Equal(resident.ToString(System.Globalization.CultureInfo.InvariantCulture), core.ReadCached(reference).Value!.Integer);
            Assert.Null(core.ReadCached(reference).Value!.Number);
            Assert.Equal(current!.CommittedGeneration, sampler.ReadLatest(request)!.CommittedGeneration);
            Assert.Equal(NpuMemoryCaptureIntegrationTests.Npu(current, name),
                NpuMemoryCaptureIntegrationTests.Npu(sampler.ReadLatest(request)!, name));
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var app = await StartHttpAsync(sampler, dashboard, slots.Provider, core, ended, timeout.Token);
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)) };
            try
            {
                var query = "?device=" + Uri.EscapeDataString(identity) + "&fields=" + name;
                using var response = await client.GetAsync("/api/metrics/fields/snapshot" + query, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var device = Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal(identity, device.GetProperty("deviceIdentity").GetString());
                var field = Assert.Single(device.GetProperty("fields").EnumerateArray());
                Assert.Equal(name, field.GetProperty("name").GetString());
                Assert.Equal("unsigned64", field.GetProperty("valueKind").GetString());
                Assert.Equal("B", field.GetProperty("unit").GetString());
                Assert.Equal(resident.ToString(System.Globalization.CultureInfo.InvariantCulture), field.GetProperty("integerValue").GetString());
                Assert.Equal(JsonValueKind.Null, field.GetProperty("numericValue").ValueKind);
                Assert.Equal("windows.npu-memory", field.GetProperty("sourceId").GetString());
                Assert.Equal(count, recording.Reads);
                Assert.Equal(current!.CommittedGeneration, sampler.ReadLatest(request)!.CommittedGeneration);
                Assert.Equal(NpuMemoryCaptureIntegrationTests.Npu(current, name),
                    NpuMemoryCaptureIntegrationTests.Npu(sampler.ReadLatest(request)!, name));
                Assert.Equal(NpuMemoryFields.SharedResident, Assert.Single(recording.Requests).Value);
                Assert.Equal(0, recording.MemoryReads);
            }
            finally { await app.StopAsync(CancellationToken.None); }
            using var retired = owner.Acquire();
            owner.RequestTopologyRefresh();
            recording.EmptyDirectory = true;
            Assert.NotNull(owner.RefreshTopologyIfRequested());
            Assert.NotEqual(retired.WorkspaceIdentity, owner.CurrentWorkspaceIdentity);
            var published = false;
            Assert.False(retired.TryPublish(_ => { published = true; return true; }, out _));
            Assert.False(published);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            await owner.StopAsync(CancellationToken.None);
        }
    }
}
