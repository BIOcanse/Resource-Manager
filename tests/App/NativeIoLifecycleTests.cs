using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

/// <summary>Product contracts exercised through the real native owner, never oracle event callbacks.</summary>
public sealed class NativeIoLifecycleTests
{
    [Fact]
    public void ReadsDoNotAcquireAndDisposalReturnsAllExplicitHolds()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        var disk = new EtwPhysicalDiskIoCaptureOwner(resource);
        var network = new EtwNetworkTcpIpCaptureOwner(resource);
        Assert.Empty(disk.Read(true).Processes); Assert.Empty(network.Read(true).RawProcessValues);
        Assert.Equal(0, resource.GetSnapshot().SubscriptionCount);
        disk.Start(); network.Start();
        Assert.Equal(2, resource.GetSnapshot().SubscriptionCount);
        var before = resource.GetSnapshot(); disk.Read(true); network.Read(true);
        Assert.Equal(before.SubscriptionCount, resource.GetSnapshot().SubscriptionCount);
        Assert.Equal(before.EnabledKeywords, resource.GetSnapshot().EnabledKeywords);
        disk.Dispose(); network.Dispose();
        Assert.Equal(0, resource.GetSnapshot().SubscriptionCount);
        Assert.Throws<ObjectDisposedException>(() => disk.Read(true));
        Assert.Throws<ObjectDisposedException>(() => network.Read(true));
        Assert.Throws<ObjectDisposedException>(disk.Start); Assert.Throws<ObjectDisposedException>(network.Start);
        disk.Dispose(); network.Dispose();
        Assert.Equal(0, resource.GetSnapshot().SubscriptionCount);
        Assert.Equal(typeof(ProcessMetricFieldRegistry).Assembly, typeof(EtwPhysicalDiskIoCaptureOwner).Assembly);
    }

    [Fact]
    public void DiskBirthScopesAndExactCountsComeFromTheNativePublication()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        resource.Identities[41] = new(100);
        long tick = 1000;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(resource, new(), () => tick);
        disk.Start(); resource.Disk(2000, 41, 0, 4096, false, 101); resource.Disk(2000, 41, 1, 17, true, 101);
        tick = 2999; resource.Publish(tick);
        Assert.False(disk.Read(true).ObservedTraffic!.Complete);
        tick = 3100; resource.Publish(tick);
        var snapshot = disk.Read(true); var window = snapshot.ObservedTraffic!;
        Assert.True(window.Complete); Assert.Equal(2000, window.WindowMilliseconds);
        Assert.Equal(2, window.Disks.Count()); Assert.Equal(2, window.Processes.Count());
        Assert.All(window.Disks, scope => Assert.Equal(resource.Generation, scope.SessionGeneration));
        Assert.All(window.Processes, row => Assert.Equal("100", row.CreationWindowsFileTime100Ns));
        Assert.Equal("4096", window.Processes.Single(row => row.Disk.DiskNumber == 0).ReadBytes);
        Assert.Equal("17", window.Processes.Single(row => row.Disk.DiskNumber == 1).WriteBytes);
        Assert.Equal(2048, snapshot.Processes[41].ReadBytesPerSecond);
        Assert.Equal(8.5, snapshot.Processes[41].WriteBytesPerSecond);
        Assert.Equal(1, snapshot.Processes[41].ReadEventCount); Assert.Equal(1, snapshot.Processes[41].WriteEventCount);
    }

    [Fact]
    public void FatalOwnerStateCannotReturnTheLastCompleteDiskOrNetworkWindow()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        long tick = 1000;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(resource, new(), () => tick);
        using var network = new EtwNetworkTcpIpCaptureOwner(resource, new(), () => tick);
        disk.Start(); network.Start(); resource.Disk(1500, 41, 0, 4096, false, 100);
        tick = 3101; resource.Publish(tick);
        Assert.True(disk.Read(true).ObservedTraffic!.Complete); Assert.NotNull(network.Read(true).ObservedTraffic);
        resource.Fail(7);
        var failedResource = resource.GetSnapshot();
        var d = disk.Read(true); var n = network.Read(true);
        Assert.Equal(PhysicalDiskIoProviderStates.Unavailable, d.ProviderState.State);
        Assert.Equal(NetworkAttributionProviderStates.Unavailable, n.ProviderState.State);
        Assert.Equal(failedResource.Message, d.ProviderState.Message); Assert.Equal(failedResource.Message, n.ProviderState.Message);
        Assert.Null(d.ObservedTraffic); Assert.Null(n.ObservedTraffic);
        Assert.Empty(d.Processes); Assert.Empty(n.RawProcessValues);
    }
}
