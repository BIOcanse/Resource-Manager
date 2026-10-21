using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaMetricWriteOwnerLibraryIntegrationTests
{
    [Fact]
    public void OriginalPercentageFormatAndNamedWarningTemplatesStayWithHostFormatter()
    {
        var actual = ResourceManager.App.Infrastructure.Control.Writers.NvidiaGpuControlCopy.Describe(
            NvidiaControlMessage.M17, new object?[] { 80.25, 120.75 });
        Assert.Equal($"这块卡的功耗上限要走 NVAPI 通道（可调 {80.25:0}%~{120.75:0}%），写入还没接入。", actual);
        Assert.Equal("显卡拒绝了写入：{Object} / {Capability} = {Value}，NVML 状态码 {Code}",
            ResourceManager.App.Infrastructure.Control.Writers.NvidiaGpuControlCopy.Describe(NvidiaControlMessage.M10, Array.Empty<object?>()));
        Assert.Equal("锁显卡时钟失败：{Capability} → [{Min}, {Max}] MHz，NVML 返回 {Code}。",
            ResourceManager.App.Infrastructure.Control.Writers.NvidiaGpuControlCopy.Describe(NvidiaControlMessage.M35, Array.Empty<object?>()));
        Assert.Equal("写显卡温度阈值失败：{Capability} = {Celsius} °C，NVML 返回 {Code}。",
            ResourceManager.App.Infrastructure.Control.Writers.NvidiaGpuControlCopy.Describe(NvidiaControlMessage.M45, Array.Empty<object?>()));
    }
    [Fact]
    public async Task QueryDoesNotProbeAndWriteRevalidatesIdentityRangeWithoutPublishingCurrent()
    {
        var inventory = Inventory(Adapter(3));
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        var writer = new NvidiaMetricFieldWriter(resource, () => inventory);
        var binding = Binding(inventory.Adapters[0]);
        var field = new FieldReference(binding.DeviceIdentity, binding.Field.Name);
        Assert.StartsWith("windows-gpu|", binding.DeviceIdentity);
        Assert.Equal(WindowsHardwareMetricTopologySource.CreateExactGpuKey(inventory.Adapters[0]), binding.DeviceIdentity);
        Assert.False(writer.Query(binding).Supported);
        Assert.Equal(0, nvml.Probes);
        var snapshot = Snapshot(binding);
        var current = new LastSuccessfulHardwareMetricSnapshot();
        current.Publish(snapshot);
        writer.Discover(snapshot, false);
        var probes = nvml.Probes;
        Assert.True(writer.Query(binding).Supported);
        Assert.Equal(probes, nvml.Probes);
        var outOfRange = await writer.WriteAsync(field, binding, new(Number: 301), default);
        Assert.Equal("failed", outOfRange.Status);
        Assert.Equal(0, nvml.Writes);
        // Ordinal changed while LUID and exact PNP/PCI identity remained the same.
        inventory = Inventory(Adapter(9));
        var result = await writer.WriteAsync(field, binding, new(Number: 210), default);
        Assert.Equal("applied", result.Status);
        Assert.Equal(1, nvml.Writes);
        Assert.Equal(210, nvml.LastWatts);
        Assert.Equal(9, nvml.LastIndex);
        Assert.Same(snapshot, current.Read());
        Assert.Empty(snapshot.Items); // A setter outcome does not fabricate a metric value.
        // Same LUID cannot authorize another PNP identity; validate before any driver probe/setter.
        var changedPnp = Adapter(9);
        changedPnp = changedPnp with
        { PhysicalIdentities = changedPnp.PhysicalIdentities.Select(identity => identity with { PnpInstanceId = identity.PnpInstanceId + "-OTHER" }).ToArray() };
        inventory = Inventory(changedPnp);
        var beforeMismatchProbe = nvml.Probes;
        var mismatch = await writer.WriteAsync(field, binding, new(Number: 210), default);
        Assert.Equal("failed", mismatch.Status);
        Assert.Equal("nvidia-device-identity-unavailable", mismatch.Reason);
        Assert.Equal(beforeMismatchProbe, nvml.Probes);
        Assert.Equal(1, nvml.Writes);
        inventory = Inventory(Adapter(9), Adapter(11));
        Assert.Equal("failed", (await writer.WriteAsync(field, binding, new(Number: 210), default)).Status);
        Assert.Equal(1, nvml.Writes);
        inventory = Inventory();
        Assert.Equal("failed", (await writer.WriteAsync(field, binding, new(Number: 210), default)).Status);
        Assert.Equal(1, nvml.Writes);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task OriginalUnsupportedDetectionAndPermissionFailureStayDistinct(int code, bool unsupported)
    {
        var nvml = new RecordedNvml { Code = code };
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        var target = new NvidiaControlObject("recorded-gpu", 0);
        var capability = new NvidiaControlCapability("gpu.power-limit", new(100, 300, 1, "W", 200));
        var outcome = await resource.WriteAsync(target, capability, new(capability.Id, 220, "W"), default);
        Assert.Equal("failed", outcome.Status);
        Assert.Equal(code, outcome.NativeStatus);
        Assert.Equal(unsupported, resource.HasUnsupportedWrite(target.Id, capability.Id));
        resource.ResetDetection();
        Assert.False(resource.HasUnsupportedWrite(target.Id, capability.Id));
    }

    [Fact]
    public async Task CancellationAndDisposalCannotReachSetter()
    {
        var nvml = new RecordedNvml();
        var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        var target = new NvidiaControlObject("gpu", 0);
        var capability = new NvidiaControlCapability("gpu.power-limit");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(target, capability, new(capability.Id, 200), cancellation.Token));
        resource.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resource.Probe(target, capability));
        Assert.Equal(0, nvml.Writes);
    }

    [Fact]
    public async Task OriginalPairedClockStateAndSiblingRetirementUseOneResource()
    {
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        var target = new NvidiaControlObject("gpu", 0);
        var minimum = new NvidiaControlCapability("gpu.core-clock-minimum");
        var maximum = new NvidiaControlCapability("gpu.core-clock-maximum");
        Assert.Equal("applied", (await resource.WriteAsync(target, minimum, new(minimum.Id, 500), default)).Status);
        Assert.Equal("applied", (await resource.WriteAsync(target, maximum, new(maximum.Id, 2000), default)).Status);
        Assert.Equal((500u, 2000u), nvml.ClockPair);
        Assert.True(await resource.RestoreAsync(target, minimum, new HashSet<string> { maximum.Id }, default));
        Assert.Equal(0, nvml.Resets);
        Assert.True(await resource.RestoreAsync(target, maximum, new HashSet<string>(), default));
        Assert.Equal(1, nvml.Resets);
        Assert.Equal("applied", (await resource.WriteAsync(target, maximum, new(maximum.Id, 1800), default)).Status);
        Assert.Equal((0u, 1800u), nvml.ClockPair);
    }
    private static HardwareMetricSnapshot Snapshot(MetricDeviceFieldBinding binding)
        => new(default, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>())
        { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding } };
    private static MetricDeviceFieldBinding Binding(WindowsGpuAdapter adapter)
    {
        // Exercise the actual default catalog projection; its DeviceIdentity is ExactIdentityKey.
        // "gpu|" belongs to the native scope handle allocation key, not the canonical device identity.
        var configuration = WindowsMetricConfiguration.CreateDefault();
        var catalog = NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration,
            [new(adapter.Index, adapter.Name, WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter),
                WindowsGpuAdapterIdentity.Pack(adapter.Luid), 1, 1, true)], [], [], new([]));
        var metric = catalog.MetricBindings.Values.Single(item => item.Field?.Code == MetricFieldCode.GpuPowerLimit
            && item.SourceId == NativeMetricSnapshotSources.NvidiaNvml);
        return new(metric.DeviceIdentity, metric.Field!, metric.MetricId, metric.SourceId)
        { AdapterKey = catalog.GpuScopeBindings[metric.ScopeHandle].AdapterLuid };
    }
    private static WindowsGpuAdapter Adapter(int index)
        => new(index, "Recorded NVIDIA", 0x10DE, 0x1234, 0, new AdapterLuid { LowPart = 5 }, false, 8, WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = [new(0, "PCI\\VEN_10DE&DEV_1234\\RECORDED", WindowsGpuPnpKind.Pci,
                true, true, 0, 1, 0, 0, 0x10DE, 0x1234, 0, 0, 0, default, WindowsGpuPhysicalIdentityStatus.Bound, 0)]
        };
    private static WindowsGpuAdapterInventoryRead Inventory(params WindowsGpuAdapter[] adapters)
        => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, (uint)adapters.Length, 0, 0, 1, adapters);
    private sealed class RecordedNvapi : NvidiaNvapiControlBridge
    {
        public override IntPtr? FindHandleByAdapterIndex(int index) => null;
        public override IntPtr? FindHandleByIdentity(WindowsGpuAdapterInventoryRead inventory, ulong key) => null;
    }
    private sealed class RecordedNvml : NvidiaNvmlControlBridge
    {
        internal int Writes, Probes, LastIndex, Code;
        internal double LastWatts;
        internal (uint, uint) ClockPair;
        internal int Resets;
        public override double? ReadMaximumClockMhz(IntPtr device, uint type) => 3000;
        public override int WriteLockedClocks(IntPtr device, uint type, uint min, uint max) { ClockPair = (min, max); return 0; }
        public override int ResetLockedClocks(IntPtr device, uint type) { Resets++; return 0; }
        public override IntPtr? FindHandleByAdapterIndex(int index) => new IntPtr(1);
        public override IntPtr? FindHandleByIdentity(WindowsGpuAdapterInventoryRead inventory, ulong key)
        { LastIndex = inventory.Adapters.Single(adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid) == key).Index; return new IntPtr(1); }
        public override NvidiaPowerLimitWatts? ReadPowerLimit(IntPtr device)
        { Probes++; return new(100, 300, 200, 200); }
        public override int WritePowerLimitWatts(IntPtr device, double watts)
        { Writes++; LastWatts = watts; return Code; }
    }
}
