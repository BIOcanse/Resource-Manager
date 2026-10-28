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

public sealed class DisplayIdVicTimingProductTests
{
    [Fact]
    public void DisplayIdVIIIOriginalVicRealTypedDomainAndInformationPreserveDeclarations()
    {
        var path = DisplayIdVicTimingTestData.Path();
        Assert.Same(path.Edid!.ModeCapability, WindowsDisplayPathTopologyReader.ToInformation(path, "recorded").ModeCapabilityDeclared);
        var snapshot = DisplayEdidModeTestData.Snapshot(path);
        Assert.Same(path.Edid.ModeCapability, Assert.Single(snapshot.Ports).Display!.ModeCapabilityDeclared);
        DisplayIdVicTimingTestData.AssertValue(DisplayIdVicTimingTestData.Field(DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow)).Value!.Value);
        Assert.Equal(DisplayIdVicTimingTestData.Bytes(), Convert.FromBase64String(path.EdidRawDeclaredBase64!));
    }

    [Fact]
    public void DisplayIdVIIIOriginalVicOpaqueExportImportRetainsRawCodeSourcesAndOriginalSelections()
    {
        var path = DisplayIdVicTimingTestData.Path(); var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow), x => x.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths([new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        Assert.Equal(JsonSerializer.Serialize(path.Edid!.ModeCapability), JsonSerializer.Serialize(roundtrip.Edid!.ModeCapability));
        Assert.Equal(path.EdidRawDeclaredBase64, roundtrip.EdidRawDeclaredBase64);
        DisplayIdVicTimingTestData.AssertValue(DisplayIdVicTimingTestData.Field(DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(roundtrip), DateTimeOffset.UtcNow)).Value!.Value);
    }

    [Fact]
    public void DisplayIdVIIIOriginalVicLegacyMissingPropertyNeverReparsesPreservedRaw()
    {
        var path = DisplayIdVicTimingTestData.Path(); var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var node = JsonSerializer.SerializeToNode(path, options)!.AsObject(); Assert.True(node["edid"]!.AsObject().Remove("modeCapability"));
        var legacy = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.Equal(path.EdidRawDeclaredBase64, legacy.EdidRawDeclaredBase64); Assert.Null(legacy.Edid!.ModeCapability);
        var info = DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(legacy), DateTimeOffset.UtcNow);
        Assert.Null(DisplayIdVicTimingTestData.Field(info).Value);
        Assert.Equal(path.EdidRawDeclaredBase64, DisplayIdVicTimingTestData.Field(info, "display_all_edid_raw_declared").Value!.Value.GetString());
    }

    [Theory]
    [InlineData(9, 17u)]
    [InlineData(11, 2u)]
    public void DisplayIdVIIIOriginalVicCodeAloneChangesOpaqueAndSemanticIdentity(int modeIndex, uint replacementCode)
    {
        var first = DisplayIdVicTimingTestData.Path(); var capability = first.Edid!.ModeCapability!;
        var second = first with { Edid = first.Edid with { ModeCapability = capability with { Modes = capability.Modes.SetItem(modeIndex, capability.Modes[modeIndex] with { Code = replacementCode }) } } };
        Assert.Equal(first.EdidRawDeclaredBase64, second.EdidRawDeclaredBase64);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(second));
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(first)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(second)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplayIdVIIIOriginalVicOwnerCanceledOrFrozenReadCannotReplaceOriginalCurrent(bool cancel)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var cancellation = new CancellationTokenSource();
        var reads = 0; var persisted = 0;
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpi(), new(new Reader(() => {
            if (Interlocked.Increment(ref reads) == 2) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return DisplayIdVicTimingTestData.Snapshot() with { Ports = [] }; }
            return DisplayIdVicTimingTestData.Snapshot();
        }), new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            (_, _) => { persisted++; return Task.CompletedTask; }, static _ => new("recorded-viii-failure", []), static (_, _, _, _) => { }, static _ => { }, static () => { }));
        await owner.CaptureInformationAsync(timeout.Token); var original = owner.ReadState();
        DisplayIdVicTimingTestData.AssertValue(DisplayIdVicTimingTestData.Field(original.InformationFields).Value!.Value);
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
    private sealed class NoAcpi : IAcpiFirmwareTableReader { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("DisplayID never reads ACPI"); }
}
