using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyVolumePublicationTests
{
    [Fact]
    public async Task OwnerDrainsItsPublicationBeforeAnotherCaptureCanReplaceTheCurrent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? published = null;
        async Task Publish(DeviceInformationSnapshot fields)
        {
            published = Assert.Single(Assert.Single(fields.Devices).Fields).Value?.GetString();
            if (published == "90") { entered.TrySetResult(); await release.Task.WaitAsync(timeout.Token); }
        }
        var snapshot = DeviceInformationTestData.Snapshot() with {
            VolumeSpaceInformation = [new(VolumeSpaceInformationCollectorTests.IdA) { AvailableBytes = 500 }]
        };
        var policy = new DeviceTopologyCapturePolicy(new Reader(snapshot), new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Project, static (_, _) => Task.CompletedTask,
            static _ => new("failed", []), static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new Firmware(), policy, volumeFreeSpacePublished: Publish);
        await owner.CaptureInformationAsync(timeout.Token);
        var api = new VolumeSpaceInformationCollectorTests.Api();
        api.Data.SectorsPerAllocationUnit = 1;
        api.Data.BytesPerSector = 1;
        api.Data.ActualAvailableAllocationUnits = 90;
        var reader = new WindowsVolumeSpaceInformationReader(api);
        var first = owner.SampleVolumeFreeSpaceAsync(reader, [VolumeSpaceInformationCollectorTests.IdA], null, timeout.Token, requireDemand: false);
        await entered.Task.WaitAsync(timeout.Token);
        api.Data.ActualAvailableAllocationUnits = 80;
        var second = owner.SampleVolumeFreeSpaceAsync(reader, [VolumeSpaceInformationCollectorTests.IdA], null, timeout.Token, requireDemand: false);
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Single(api.Requested);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        Assert.Equal("80", published);
        Assert.Equal(published, Assert.Single(Assert.Single(owner.ReadVolumeFreeSpace().Devices).Fields).Value?.GetString());
        Assert.Equal(0, api.Directories);
        owner.InitializePersisted(DeviceTopologyStructure.Create(snapshot), "persisted", 1);
        Assert.Null(published);
        Assert.Null(Assert.Single(Assert.Single(owner.ReadVolumeFreeSpace().Devices).Fields).Value);
    }
    private sealed class Reader(DeviceTopologySnapshot snapshot) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => snapshot; }
    private sealed class Firmware : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }
}
