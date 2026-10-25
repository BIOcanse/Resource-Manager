using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using ResourceManager.App.Application.Components;
using ResourceManager.ReadWrite.Dependencies;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

// Reuse the original recorded native-workspace/tracker fixture, not a second
// capture owner. The test's CPU identity/topology and hardware are all recorded.
public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public async Task CanonicalControlUsesOriginalHostCurrentAndPermissionLayerWithoutQueryProbe()
    {
        using var fixture = new Fixture(Topology([]));
        var directory = Path.Combine(Path.GetTempPath(), "host-canonical-" + Guid.NewGuid().ToString("N"));
        var hardware = new CanonicalRecordedIntel();
        using var writer = new IntelCpuControlWriter(() => hardware, Path.Combine(directory, "baseline.json"));
        var access = new CanonicalAccess();
        var controls = new CanonicalControls();
        var identityReads = 0;
        var recordedIdentity = new CpuIdentityInformation("GenuineIntel", 6, 154, 1, "CPU", [], []);
        var threadCpuid = new CpuThreadCpuidData(recordedIdentity, 0x906A1, null, null, null, null, null, []);
        var controlResources = new WindowsMetricControlResources
        {
            IntelCpuControl = writer.Resource,
            ControlReadCpuIdentity = () => { identityReads++; return recordedIdentity; },
            ControlReadCpuTopology = () => new([new("core:recorded", 0, true, [0, 1])], [])
                { Packages = [new("package:recorded", [0, 1])], ThreadCpuid = [new(0, threadCpuid), new(1, threadCpuid)] },
            ControlResolveObjectId = (_, _, _, _, _) => "cpu:product-recorded"
        };
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id => new MonitoringSourceZone(id)));
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!,
            new WindowsGpuAdapterOrderMonitoringZone(() => new WindowsGpuAdapterInventoryRead(
                SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, [])),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!,
            fixture.Owner, fixture.Catalog, registry, fixture.Slots.Owner, fixture.Slots.Provider,
            NullLogger<WindowsHardwareMetricSampler>.Instance, borrowedControls: controlResources);
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [writer], access));
        try
        {
            var catalog = await backend.DiscoverAsync(default);
            var field = catalog.Fields.Single(item => item.Definition.Name == "cpu_intel_requested_pl1_power_limit_watts").Field;
            var afterDiscover = hardware.Reads;
            var identityAfterDiscover = identityReads;
            Assert.True(backend.Catalog.Require(field).Write.Supported);
            Assert.NotNull(backend.Catalog.Require(field).WriteContract);
            Assert.Null(backend.ReadCached(field).Value);
            Assert.Equal(afterDiscover, hardware.Reads);
            Assert.Equal(identityAfterDiscover, identityReads);
            Assert.Equal(0, hardware.PowerWrites);
            var subscriptions = new HostFieldSubscriptionBackend(backend, sampler, sampler);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var stream = subscriptions.SubscribeAsync("control-current", [field], TimeSpan.FromSeconds(1), timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var next = stream.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            await sampler.StartAsync(timeout.Token);
            Assert.NotNull(sampler.CaptureSamplingWorkerTask());
            Assert.Equal(45, Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value!.Number);
            Assert.True(await next);
            FieldReadingAssert.CurrentOrNewer(Assert.Single(stream.Current), backend.ReadCached(field));
            Assert.NotNull(sampler.CaptureSamplingWorkerTask());
            await sampler.StopAsync(CancellationToken.None);
            Assert.Null(sampler.CaptureSamplingWorkerTask());
            // The periodic collector may include a native catalog refresh; it must
            // not repeat control Probe/baseline IO while reading configured current.
            var probeChecks = hardware.ProbeChecks;
            await sampler.CaptureSnapshotAsync(MetricSampleRequest.All, default);
            Assert.Equal(probeChecks, hardware.ProbeChecks);
            var address = fixture.Catalog.Current!.FieldBindings.Values.Single(binding => binding.Field.Name == field.FieldName).RequestAddress;
            await sampler.CaptureSnapshotAsync(MetricSampleRequest.ForIds([address]).WithCatalog(), default);
            Assert.Equal(probeChecks, hardware.ProbeChecks);
            Assert.Equal(45, backend.ReadCached(field).Value!.Number);
            Assert.Equal(0, hardware.PowerWrites);
            Assert.Equal("unsupported", (await backend.WriteAsync(field, new(Number: 50), default)).Status);
            Assert.Equal(0, hardware.PowerWrites);
            access.Current = ControlAccessLevels.Root;
            var applied = await backend.WriteAsync(field, new(Number: 50), default);
            Assert.Equal("applied", applied.Status);
            Assert.Equal(FieldWriteEvidence.MaskedRegisterReadback, applied.Evidence!.Kind);
            Assert.Equal(1, hardware.PowerWrites);
            Assert.Equal(45, backend.ReadCached(field).Value!.Number); // no speculative write-through
            Assert.Equal(50, Assert.Single(await backend.CollectAsync([field], default)).Value!.Number);
            Assert.Equal("failed", (await backend.WriteAsync(field, new(Number: 500), default)).Status);
            Assert.Equal(1, hardware.PowerWrites);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync();
            try
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.WriteAsync(field, new(Number: 55), canceled.Token));
                Assert.Equal(1, hardware.PowerWrites);
            }
            finally { gate.Release(); }
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ManagedFanLeaseUsesTypedGpuInventoryDependencyOnOriginalTracker()
    {
        using var fixture = new Fixture(Topology([]));
        var entry = WindowsMetricControlBindings.FindByName("fan_amd_requested_target_speed_rpm")!;
        const string address = "managed-control|recorded-gpu-fan|fan_amd_requested_target_speed_rpm";
        var binding = new MetricDeviceFieldBinding("recorded-gpu-fan", entry.Definition, address, entry.SourceId)
            { AdapterKey = 123, ControlTarget = new(GpuDeviceIdentity: "recorded-gpu", GpuPnp: "PCI\\RECORDED")
                { RawObjectId = "fan:product-recorded" } };
        using (var lease = fixture.Owner.Acquire())
        {
            var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity);
            fixture.Catalog.PublishCatalogProbe(snapshot with
            {
                FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>(snapshot.FieldBindings)
                    { [address] = binding }
            });
        }
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        var controls = new CanonicalControls();
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [], new CanonicalAccess()));
        var subscriptions = new HostFieldSubscriptionBackend(backend, sampler, sampler);
        using var demand = subscriptions.Acquire("recorded-fan", [new(binding.DeviceIdentity, entry.Definition.Name)], TimeSpan.FromSeconds(1));
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        var plan = tracker.CreatePlan(DateTimeOffset.UtcNow);
        try
        {
            Assert.NotNull(plan.Request);
            Assert.Contains(address, plan.DueItemIds);
            Assert.Contains(SamplingDatasetIds.SystemGpuInventory, plan.DueItemIds);
            Assert.Equal(2, plan.Schedule.ActiveItemCount);
            Assert.Contains(SamplingDatasetIds.SystemGpuInventory, sampler.GetPublicationItemIds(plan.Request!));
            Assert.DoesNotContain(SamplingDatasetIds.SystemGpuInventory,
                sampler.GetPublicationItemIds(MetricSampleRequest.ForIds([address])));
            Assert.Null(sampler.CaptureSamplingWorkerTask());
        }
        finally
        {
            if (plan.Request is not null || !plan.Triggers.IsEmpty)
                tracker.CompletePlan(plan, DateTimeOffset.UtcNow,
                    ResourceManager.Subscription.Native.NativeSamplingSubscriptionCompletionStatus.Skipped);
        }
    }
    [Fact]
    public void EveryCanonicalDeclaredDependencyHasAnExactHostQueryBinding()
    {
        using var client = new HttpClient();
        var providers = new CanonicalDependencyProviders();
        var nativeQueries = new List<string>();
        var dependencies = HostReadWriteDependencies.Create(client, providers,
            Path.Combine(Path.GetTempPath(), "recorded-not-created-" + Guid.NewGuid().ToString("N")), source =>
            {
                nativeQueries.Add(source);
                return new(source, DependencyAvailability.Missing, "recorded-missing");
            });
        var declared = WindowsMetricControlBindings.All
            .SelectMany(entry => entry.ReadDependencies.Concat(entry.WriteDependencies))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(providers.Queries);
        Assert.Empty(nativeQueries);
        Assert.Equal(new[] { "amd-adlx-provider", "hardware-bridge", "intel-graphics-driver", "intel-msr-pawnio-provider" }, declared);
        foreach (var id in declared) Assert.Equal(id, dependencies.Query(id).Id);
        Assert.Equal(new[] { "amd-adlx-provider", "hardware-bridge", "intel-msr-pawnio-provider" }, providers.Queries);
        Assert.Equal(new[] { "intel.igcl" }, nativeQueries);
        Assert.DoesNotContain("amd-smu-pawnio-provider", providers.Queries);
    }

    private sealed class CanonicalDependencyProviders : IProviderRuntimeProbe
    {
        internal List<string> Queries { get; } = [];
        public ComponentProviderRuntimeProbeResult Probe(string componentId)
        {
            Queries.Add(componentId);
            return new(true, true, "RuntimeAvailable", "recorded; no hardware session", null);
        }
    }
    private sealed class CanonicalControls : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => new([new("cpu:product-recorded", ControlObjectKinds.Cpu, "CPU",
            new(ControlOperatingSystems.Windows, ControlVendors.Intel),
            [new("cpu.power-limit", ControlValueKinds.Number, true, Range: new(5, 200, 1, "W"),
                RequiredAccessLevel: ControlAccessLevels.Root)])], DateTimeOffset.UtcNow);
    }
    private sealed class CanonicalAccess : IControlAccessLevel
    {
        public string Current { get; set; } = ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) { Current = level; return Task.CompletedTask; }
    }
    private sealed class CanonicalRecordedIntel : IIntelCpuHardware
    {
        public int Reads { get; private set; }
        public int PowerWrites { get; private set; }
        public int ProbeChecks { get; private set; }
        private ulong limits = 45UL | (1UL << 15) | (90UL << 32) | (1UL << 47);
        public ulong ReadMsr(uint register) { Reads++; return register == 0x610 ? limits : 0; }
        public bool CanWriteMsr(uint register) { ProbeChecks++; return true; }
        public void WriteMsr(uint register, ulong value) { if (register == 0x610) { limits = value; PowerWrites++; } }
        public void Dispose() { }
    }
}
