using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    // Explicit diagnostic run only; the normal regression suite never probes the
    // user's hardware or assumes a performance threshold on a loaded machine.
    [Fact]
    public async Task MeasureBarCaptureOnCurrentMachineWhenExplicitlyRequested()
    {
        var destination = Environment.GetEnvironmentVariable("RM_BAR_CAPTURE_PROBE");
        if (string.IsNullOrWhiteSpace(destination)) return;
        string[] ids = ["cpu.usage", "disk.io", "gpu.0.usage", "gpu.0.vram", "gpu.1.usage", "memory.usage", "network.traffic", "virtualMemory.usage"];
        using var hardwareOwner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default);
        using var pdh = new NativePdhSession(new NativePdhApi(), WindowsMetricConfiguration.Default.PdhOptions, TimeProvider.System);
        pdh.Read(new WindowsGpuAdapterOrderReader().ReadInventory().TopologyFingerprint);
        var hardwareRequest = MetricSampleRequest.ForIds(["cpu.usage", "memory.usage", "virtualMemory.usage", "gpu.0.usage", "gpu.0.vram", "gpu.1.usage"]);
        await hardwareOwner.CaptureAsync(hardwareRequest);
        await Task.Delay(1000);
        var hardware = await hardwareOwner.CaptureAsync(hardwareRequest);
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(hardware), catalog,
            new StaticResourceResidualBreakdownProvider(), new ProbeDisk(), new ProbeNetwork(), subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader(pdh), WindowsProcessInventoryReader.Instance);
        var rows = new List<object>();
        for (var index = 0; index < 20; index++)
        {
            var timer = Stopwatch.StartNew();
            var read = pdh.Read();
            var pdhMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var snapshot = await sampler.CaptureSnapshotAsync(ResourceBreakdownSampleRequest.ForResourceTable(ids,
                new Dictionary<string, string>()), CancellationToken.None);
            rows.Add(new { index, captureMs = timer.Elapsed.TotalMilliseconds, pdhMs, read.ResultCode,
                read.NativeStatus, read.Availability, engineStatus = read.Snapshot?.GpuEngineObservation.Availability,
                bars = snapshot.Bars.Count, emptyBars = snapshot.Bars.Count(bar => bar.TotalValue is null) });
            await Task.Delay(1000);
        }
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(rows));
    }

    private sealed class ProbeDisk : IPhysicalDiskIoAttributionReader
    {
        public PhysicalDiskIoAttributionSnapshot Read() => new(DateTimeOffset.UtcNow,
            new Dictionary<int, PhysicalDiskProcessAttribution>(), new("probe", PhysicalDiskIoProviderStates.Running, ""));
    }
    private sealed class ProbeNetwork : INetworkAttributionReader
    {
        public NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request) => new(DateTimeOffset.UtcNow,
            new Dictionary<int, NetworkProcessAttribution>(), new("probe", NetworkAttributionProviderStates.Running, ""));
    }
}
