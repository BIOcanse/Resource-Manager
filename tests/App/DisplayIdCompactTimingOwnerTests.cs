using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdCompactTimingOwnerTests
{
    [Fact]
    public async Task RealParserOriginalOwnerDirectCacheFailureRawAndRecoveryUseOneCurrent()
    {
        var phase = 0; var reads = 0;
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpi(), new(new Reader(() => {
            reads++; if (phase == 2) throw new IOException("recorded compact timing source failure");
            var d = DisplayIdCompactTimingTestData.Descriptor(); if (phase == 1) d[3] |= 1;
            var read = WindowsMonitorEdidReader.CreateReadResult(DisplayIdCompactTimingTestData.Bytes(d));
            return DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(read) with { Active = false, TargetAvailable = false });
        }), new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            static (_, _) => Task.CompletedTask, static _ => new("recorded-compact-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, e => e.Field.FieldName == "display_all_mode_capability").Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var before = reads; AssertTiming(backend.ReadCached(field).Value!.Structure!.Value); Assert.Equal(before, reads);
        var direct = Assert.Single(await backend.CollectAsync([field], CancellationToken.None));
        AssertTiming(direct.Value!.Structure!.Value); Assert.Equal(direct.ObservedAt, backend.ReadCached(field).ObservedAt);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var saved = owner.ReadState(); before = reads;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([field], canceled.Token));
        Assert.Equal(before, reads); Assert.Same(saved, owner.ReadState());
        phase = 1;
        var unknown = await backend.CollectAsync([field, rawField], CancellationToken.None);
        Assert.Null(Assert.Single(unknown, row => row.Field == field).Value);
        var bad = DisplayIdCompactTimingTestData.Descriptor(); bad[3] |= 1;
        Assert.Equal(DisplayIdCompactTimingTestData.Bytes(bad), Assert.Single(unknown, row => row.Field == rawField).Value!.Bytes!.Value.ToArray());
        phase = 2; Assert.Null(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value);
        phase = 0; AssertTiming(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value!.Structure!.Value);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, e => e.Field.FieldName == "display_all_mode_capability").Field);
    }

    private static void AssertTiming(JsonElement value)
    {
        var mode = Assert.Single(value.GetProperty("modes").EnumerateArray()); var timing = mode.GetProperty("timing");
        Assert.Equal("displayid-ii", mode.GetProperty("encoding").GetString());
        Assert.Equal(65_000_000UL, timing.GetProperty("pixelClockHz").GetUInt64());
        Assert.Equal(3u, timing.GetProperty("verticalSyncOffset").GetUInt32());
        Assert.Equal(5u, timing.GetProperty("verticalSyncWidth").GetUInt32());
        Assert.True(timing.GetProperty("positiveVerticalSync").GetBoolean());
    }

    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
}
