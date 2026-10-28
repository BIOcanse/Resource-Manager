using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdBitmapProductTests
{
    [Theory]
    [InlineData(0x12)]
    public void BitmapTraversesRealTypedDomainAndInformation(byte containerRevision)
    {
        var path = DisplayIdBitmapTestData.Path(containerRevision);
        Assert.Same(path.Edid!.ModeCapability, WindowsDisplayPathTopologyReader.ToInformation(path, "recorded").ModeCapabilityDeclared);
        var snapshot = DisplayEdidModeTestData.Snapshot(path);
        Assert.Same(path.Edid.ModeCapability, Assert.Single(snapshot.Ports).Display!.ModeCapabilityDeclared);
        DisplayIdBitmapTestData.AssertValue(DisplayIdVicTimingTestData.Field(DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow)).Value!.Value, containerRevision);
        Assert.Equal(DisplayIdBitmapTestData.Bytes(containerRevision), Convert.FromBase64String(path.EdidRawDeclaredBase64!));
        Assert.Equal(DisplayIdVicTimingTestData.LegacyBytes().AsSpan(0, 512).ToArray(), DisplayIdBitmapTestData.Bytes(containerRevision).AsSpan(0, 512).ToArray());
    }

    [Theory]
    [InlineData(0x12)]
    public void BitmapOpaqueExportImportPreservesSeventeenDeclarations(byte containerRevision)
    {
        var path = DisplayIdBitmapTestData.Path(containerRevision);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow), x => x.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths([new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        Assert.Equal(JsonSerializer.Serialize(path.Edid!.ModeCapability), JsonSerializer.Serialize(roundtrip.Edid!.ModeCapability));
        Assert.Equal(path.EdidRawDeclaredBase64, roundtrip.EdidRawDeclaredBase64);
        DisplayIdBitmapTestData.AssertValue(DisplayIdVicTimingTestData.Field(DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(roundtrip), DateTimeOffset.UtcNow)).Value!.Value, containerRevision);
    }

    [Theory]
    [InlineData(0x12, false)]
    [InlineData(0x12, true)]
    public void BitmapBitIndexOrCodeAloneChangesOpaqueAndSemanticIdentity(byte containerRevision, bool bitIndexOnly)
    {
        var first = DisplayIdBitmapTestData.Path(containerRevision); var capability = first.Edid!.ModeCapability!;
        var mode = capability.Modes[13];
        var changed = bitIndexOnly ? mode with { BitIndex = 2 } : mode with { Code = 8 };
        var second = first with { Edid = first.Edid with { ModeCapability = capability with { Modes = capability.Modes.SetItem(13, changed) } } };
        Assert.Equal(first.EdidRawDeclaredBase64, second.EdidRawDeclaredBase64);
        Assert.Equal(mode.RawDescriptor, changed.RawDescriptor);
        if (bitIndexOnly) { Assert.Equal(mode.Code, changed.Code); Assert.NotEqual(mode.BitIndex, changed.BitIndex); }
        else { Assert.Equal(mode.BitIndex, changed.BitIndex); Assert.NotEqual(mode.Code, changed.Code); }
        var firstNode = JsonSerializer.SerializeToNode(mode)!.AsObject();
        var secondNode = JsonSerializer.SerializeToNode(changed)!.AsObject();
        Assert.True(firstNode.Remove(bitIndexOnly ? "BitIndex" : "Code"));
        Assert.True(secondNode.Remove(bitIndexOnly ? "BitIndex" : "Code"));
        Assert.Equal(firstNode.ToJsonString(), secondNode.ToJsonString());
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(second));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(first)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(second)));
    }

    [Theory]
    [InlineData(0x12, false)]
    [InlineData(0x12, true)]
    public async Task BitmapOwnerCanceledOrFrozenReadCannotReplaceOriginalCurrent(byte containerRevision, bool cancel)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var cancellation = new CancellationTokenSource();
        var reads = 0; var persisted = 0;
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpi(), new(new Reader(() => {
            if (Interlocked.Increment(ref reads) == 2) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return DisplayIdBitmapTestData.Snapshot(containerRevision) with { Ports = [] }; }
            return DisplayIdBitmapTestData.Snapshot(containerRevision);
        }), new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            (_, _) => { persisted++; return Task.CompletedTask; }, static _ => new("recorded-iv-failure", []), static (_, _, _, _) => { }, static _ => { }, static () => { }));
        await owner.CaptureInformationAsync(timeout.Token); var original = owner.ReadState();
        DisplayIdBitmapTestData.AssertValue(DisplayIdVicTimingTestData.Field(original.InformationFields).Value!.Value, containerRevision);
        var gate = new object(); var active = true;
        var permission = new DeviceTopologyCapturePermission(() => { lock(gate) return active; }, commit => { lock(gate) { if (!active) return false; commit(); return true; } }, () => true);
        using var signal = owner.SubscribeTopologyPublications(); var notification = signal.WaitAsync(timeout.Token).AsTask();
        var capture = Task.Run(() => owner.SampleTopologyAsync(permission, cancellation.Token, requireDemand: false));
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(2))); if(cancel) cancellation.Cancel(); else lock(gate) active = false; }
        finally { release.Set(); }
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await capture.WaitAsync(timeout.Token));
        Assert.Same(original, owner.ReadState()); Assert.Equal(1, persisted); Assert.Equal(2, reads); Assert.False(notification.IsCompleted);
        await timeout.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
    }
    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class NoAcpi : IAcpiFirmwareTableReader { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("Bitmap never reads ACPI"); }
}
