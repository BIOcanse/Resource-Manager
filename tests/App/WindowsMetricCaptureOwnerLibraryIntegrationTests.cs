using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsMetricCaptureOwnerLibraryIntegrationTests
{
    [Fact]
    public async Task LateQueuedCaptureObservesDisposalWithoutAnUnusableGateOrResourceDoubleRelease()
    {
        var released = new RecordedLifetime();
        var resources = UnusedResources();
        resources.Own(released);
        var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), resources, new());
        var capture = (WindowsHardwareMetricCapture)typeof(WindowsHardwareMetricOwner)
            .GetField("capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        await capture.SampleGate.WaitAsync();
        var queuedCapture = owner.CaptureAsync(MetricSampleRequest.CatalogProbe).AsTask();
        var secondQueuedCapture = owner.CaptureAsync(MetricSampleRequest.CatalogProbe).AsTask();
        var dispose = Task.Run(owner.Dispose);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Disposal marks admission closed before it can drain the held gate.
        while ((int)typeof(WindowsHardwareMetricOwner).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(owner)! == 0) await Task.Delay(1, timeout.Token);
        var lateWaiter = LateAdmittedCapture();
        capture.SampleGate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedCapture.WaitAsync(timeout.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => secondQueuedCapture.WaitAsync(timeout.Token));
        await dispose.WaitAsync(timeout.Token);
        await lateWaiter.WaitAsync(timeout.Token);
        owner.Dispose();
        Assert.Equal(1, released.Releases);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.CaptureAsync(MetricSampleRequest.CatalogProbe).AsTask());
        Assert.Throws<ObjectDisposedException>(() => owner.ReadCachedSnapshot());

        async Task LateAdmittedCapture()
        {
            // Models a caller that passed the first admission check, then was delayed before WaitAsync.
            await capture.SampleGate.WaitAsync(timeout.Token);
            try
            {
                Assert.NotEqual(0, (int)typeof(WindowsHardwareMetricOwner).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!);
            }
            finally { capture.SampleGate.Release(); }
        }
    }

    [Fact]
    public void OriginalCommitProjectionPreservesExactBytesAndRejectsInvalidPages()
    {
        var valid = WindowsVirtualMemoryMetricResource.CreateCommitMetrics(new()
            { PageSize = 4096, CommitTotal = 7, CommitLimit = 19, CommitPeak = 9 });
        Assert.Equal(7UL * 4096, valid.UsedBytes);
        Assert.Equal(19UL * 4096, valid.TotalBytes);
        Assert.Equal(SamplingObservationStatus.Current, valid.ObservationStatus);
        var invalid = WindowsVirtualMemoryMetricResource.CreateCommitMetrics(new()
            { PageSize = 4096, CommitTotal = 20, CommitLimit = 19 });
        Assert.False(invalid.IsSelectable);
        Assert.Equal(SamplingObservationStatus.Unavailable, invalid.ObservationStatus);
    }

    private sealed class RecordedLifetime : IDisposable
    { public int Releases; public void Dispose() => Releases++; }
    private static WindowsHardwareMetricResources UnusedResources() => new()
    {
        ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
        ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => throw new InvalidOperationException(),
        ReadNumaIdentities = () => throw new InvalidOperationException(), ReadGpuInventory = () => throw new InvalidOperationException(),
        ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
        ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
        ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
        ReadBatteryIdentities = () => throw new InvalidOperationException(), ReadBattery = (_, _) => throw new InvalidOperationException(),
        ReadNetworkIdentities = () => throw new InvalidOperationException(), ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(),
        ReadSystemIo = _ => throw new InvalidOperationException(), ReadStorageIdentities = _ => throw new InvalidOperationException(),
        ReadFanIdentities = _ => throw new InvalidOperationException(), ReadStorageSensors = () => throw new InvalidOperationException(),
        ReadNvmeSensors = _ => throw new InvalidOperationException(), ReadAtaSensors = _ => throw new InvalidOperationException(),
        ReadHardwareMonitor = () => throw new InvalidOperationException(), ReadNotebookFans = () => throw new InvalidOperationException(),
        SynchronizeNotebookDemand = _ => { }
    };
}
