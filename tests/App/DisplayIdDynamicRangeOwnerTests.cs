using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdDynamicRangeOwnerTests
{
    [Fact]
    public async Task RealOwnerPublishesRangeOnlyChangesAndKeepsRawIndependentOnFailure()
    {
        var phase = 0; var reads = 0;
        byte[] Bytes() {
            var payload = DisplayIdDynamicRangeFixture.Payload();
            if (phase == 1) { payload[7] = 0; payload[8] = 0x83; }
            if (phase == 2) payload[8] |= 4;
            return DisplayIdDynamicRangeFixture.Bytes(payload);
        }
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpi(), new(new Reader(() => {
            reads++; if (phase == 3) throw new IOException("recorded dynamic range failure");
            var read = WindowsMonitorEdidReader.CreateReadResult(Bytes());
            return DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(read) with { Active = false, TargetAvailable = false });
        }), new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            static (_, _) => Task.CompletedTask, static _ => new("recorded-range-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, row => row.Field.FieldName == "display_all_mode_capability").Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var before = reads; AssertRange(backend.ReadCached(field).Value!.Structure!.Value, 360); Assert.Equal(before, reads);
        var saved = owner.ReadState();
        using var signal = owner.SubscribeTopologyPublications();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var publication = signal.WaitAsync(timeout.Token).AsTask();
        phase = 1; AssertRange(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value!.Structure!.Value, 768);
        await publication.WaitAsync(timeout.Token); Assert.NotSame(saved, owner.ReadState());
        AssertRange(backend.ReadCached(field).Value!.Structure!.Value, 768);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); saved = owner.ReadState(); before = reads;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([field], canceled.Token));
        Assert.Equal(before, reads); Assert.Same(saved, owner.ReadState());
        phase = 2; var unknown = await backend.CollectAsync([field, rawField], timeout.Token);
        Assert.Null(Assert.Single(unknown, row => row.Field == field).Value);
        Assert.Equal(Bytes(), Assert.Single(unknown, row => row.Field == rawField).Value!.Bytes!.Value.ToArray());
        phase = 3; Assert.Null(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value);
        phase = 0; AssertRange(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value!.Structure!.Value, 360);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, row => row.Field.FieldName == field.FieldName).Field);
    }

    private static void AssertRange(JsonElement value, uint maximumRate)
    {
        Assert.Empty(value.GetProperty("modes").EnumerateArray());
        var range = Assert.Single(value.GetProperty("dynamicTimingRanges").EnumerateArray());
        Assert.Equal(60_000_000UL, range.GetProperty("minimumPixelClockHz").GetUInt64());
        Assert.Equal(maximumRate, range.GetProperty("maximumVerticalRateHz").GetUInt32());
        Assert.True(range.GetProperty("seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking").GetBoolean());
    }

    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
}
