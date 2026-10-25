using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace Resource_Manager_APP.Tests;

public sealed class NpuMemoryAppliedPlanSwitchTests
{
    [Fact]
    public async Task SameHostZoneUsesEachAppliedPlansCapacityAcrossOneSixtyFourOne()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var slots = new HostManagerSamplingSubscriptionTestFixture(Plan(1, 1));
        const string pnp = @"ACPI\RECORDED_NPU\PLAN_SWITCH";
        var identity = new NpuMemoryCatalogIdentity(pnp).ExactIdentityKey;
        var bounds = new ConcurrentQueue<uint>();
        var zone = new WindowsNpuMemoryMonitoringZone((devices, requested, maximum, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(identity, Assert.Single(devices).ExactIdentityKey);
            Assert.Equal(NpuMemoryFields.DedicatedCommitted, Assert.Single(requested).Value);
            bounds.Enqueue(maximum);
            return [new NpuMemoryRead(identity, maximum, null, null, null)];
        });
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(1,
                NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
                NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
                { NpuMemoryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, NpuMemoryIdentities = [new(pnp)] }),
            new RecordedEnvironment(Path.Combine(AppContext.BaseDirectory, "npu-plan-switch", Guid.NewGuid().ToString("N"))),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(timeout.Token);
        var dashboard = new HardwareMetricCatalogState();
        var registry = new MonitoringSourceZoneRegistry([new WindowsMemoryMonitoringZone(), zone]);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard, registry,
            slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, npuMemoryZone: zone);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await Observe(1);
            Assert.Empty(slots.Provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = Plan(64, 2) }).DeliveryFailures);
            await Observe(64);
            Assert.Empty(slots.Provider.Publish(CompiledRuntimePlan.Default with { Version = 3, HostManager = Plan(1, 3) }).DeliveryFailures);
            await Observe(1);
            Assert.Contains(1U, bounds);
            Assert.Contains(64U, bounds);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            await owner.StopAsync(CancellationToken.None);
        }

        async Task Observe(uint expected)
        {
            owner.RequestTopologyRefresh();
            Assert.NotNull(owner.RefreshTopologyIfRequested());
            using (var lease = owner.Acquire())
            {
                Assert.Equal(expected, lease.Workspace.ReadCatalog().NpuMemoryEffectiveMaximumInstanceCount);
                dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
            }
            var request = MetricDeviceFields.CreateRequest(dashboard.Current!, identity, ["npu_all_dedicated_memory_committed"]);
            var beforeReads = bounds.Count;
            await using var stream = sampler.SubscribeAsync("npu-plan-switch", request,
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
            // Existing current may remain displayed until the next admitted query completes.
            while (await stream.MoveNextAsync())
            {
                var value = Assert.Single(stream.Current.Items.Values, item => item.FieldName == "npu_all_dedicated_memory_committed");
                if (bounds.Count <= beforeReads || value.IntegerValue != MetricInteger.FromUnsigned128(expected))
                    continue;
                Assert.Equal(expected, bounds.Last());
                return;
            }
            Assert.Fail("The applied plan did not produce a fresh capacity-bound NPU observation.");
        }
    }

    private static CompiledHostManagerPlan Plan(int capacity, ulong epoch)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            foreach (var rule in root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray()
                         .Where(rule => rule!["source_id"]!.GetValue<string>() == "windows.npu-memory"))
                rule!["maximum_instance_count"] = capacity;
        }, planEpoch: epoch);

    private sealed class RecordedEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NpuMemoryAppliedPlanSwitchTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}