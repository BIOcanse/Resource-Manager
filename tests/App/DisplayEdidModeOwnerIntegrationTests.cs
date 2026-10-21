using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DisplayEdidModeOwnerIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalCommitGateRejectsModeFromCaptureCanceledOrFrozenWhileReading(bool cancel)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var cancellation = new CancellationTokenSource();
        var reads = 0; var persisted = 0;
        using var owner = Owner(() => {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return DisplayEdidModeInformationCollectorTests.Snapshot(DisplayEdidModeInformationCollectorTests.Path(fractional: true));
            }
            return DisplayEdidModeInformationCollectorTests.Healthy();
        }, () => persisted++);
        await owner.CaptureInformationAsync(timeout.Token);
        var original = owner.ReadState();
        var gate = new object(); var active = true;
        var permission = new DeviceTopologyCapturePermission(
            () => { lock (gate) return active; },
            commit => { lock (gate) { if (!active) return false; commit(); return true; } }, () => true);
        using var signal = owner.SubscribeTopologyPublications();
        var notification = signal.WaitAsync(timeout.Token).AsTask();
        var capture = Task.Run(() => owner.SampleTopologyAsync(permission, cancellation.Token, requireDemand: false));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            if (cancel) cancellation.Cancel(); else lock (gate) active = false;
        }
        finally { release.Set(); }
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await capture.WaitAsync(timeout.Token));
        Assert.Same(original, owner.ReadState()); Assert.Equal(1, persisted); Assert.Equal(2, reads);
        DisplayEdidModeInformationCollectorTests.AssertValue(DisplayEdidModeInformationCollectorTests.Field(owner.ReadState().InformationFields).Value!.Value);
        Assert.False(notification.IsCompleted);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
    }

    [Fact]
    public async Task AlreadyFrozenPermissionDoesNotQueryOrReplaceOriginalCurrent()
    {
        var reads = 0;
        using var owner = Owner(() => { reads++; return DisplayEdidModeInformationCollectorTests.Healthy(); });
        await owner.CaptureInformationAsync(CancellationToken.None);
        var original = owner.ReadState();
        var permission = new DeviceTopologyCapturePermission(() => false, _ => throw new InvalidOperationException("inactive permission"), () => true);
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await owner.SampleTopologyAsync(permission, CancellationToken.None));
        Assert.Equal(1, reads); Assert.Same(original, owner.ReadState());
    }

    private static DeviceTopologyCaptureOwner Owner(Func<DeviceTopologySnapshot> read, Action? persisted = null)
        => new(new NoAcpi(), new(new Reader(read), new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Create,
            (_, _) => { persisted?.Invoke(); return Task.CompletedTask; },
            static _ => new("recorded-display-failure", []), static (_, _, _, _) => { }, static _ => { }, static () => { }));
    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
}
