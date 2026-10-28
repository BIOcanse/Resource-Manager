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

public sealed class DisplayIdAdaptiveSyncOwnerTests
{
    [Fact]
    public async Task RealOwnerPublishesAdaptiveRangeChangesAndKeepsCurrentCancellationAndRawRecovery()
    {
        var phase = 0; var reads = 0;
        byte[] Bytes()
        {
            var name = phase == 1 ? "buffered-avt-quarter-ms-maximum"
                : phase == 2 ? "same-section-empty-then-nonempty" : "native-favt-zero-special";
            var row = DisplayIdAdaptiveSyncTestData.PreparedCases().Single(row => (string)row[0] == name);
            return Convert.FromBase64String((string)row[1]);
        }
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpi(), new(new Reader(() => {
            reads++; if (phase == 3) throw new IOException("recorded adaptive range failure");
            var read = WindowsMonitorEdidReader.CreateReadResult(Bytes());
            return DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(read) with { Active = false, TargetAvailable = false });
        }), new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            static (_, _) => Task.CompletedTask, static _ => new("recorded-adaptive-range-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, row => row.Field.FieldName == "display_all_mode_capability").Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var before = reads; AssertRange(backend.ReadCached(field).Value!.Structure!.Value, 144); Assert.Equal(before, reads);
        var saved = owner.ReadState();
        using var signal = owner.SubscribeTopologyPublications();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var publication = signal.WaitAsync(timeout.Token).AsTask();
        phase = 1; AssertRange(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value!.Structure!.Value, 1024);
        await publication.WaitAsync(timeout.Token); Assert.NotSame(saved, owner.ReadState());
        AssertRange(backend.ReadCached(field).Value!.Structure!.Value, 1024);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); saved = owner.ReadState(); before = reads;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([field], canceled.Token));
        Assert.Equal(before, reads); Assert.Same(saved, owner.ReadState());
        phase = 2; var unknown = await backend.CollectAsync([field, rawField], timeout.Token);
        Assert.Null(Assert.Single(unknown, row => row.Field == field).Value);
        Assert.Equal(Bytes(), Assert.Single(unknown, row => row.Field == rawField).Value!.Bytes!.Value.ToArray());
        phase = 3; Assert.Null(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value);
        phase = 0; AssertRange(Assert.Single(await backend.CollectAsync([field], timeout.Token)).Value!.Structure!.Value, 144);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, row => row.Field.FieldName == field.FieldName).Field);
    }

    private static void AssertRange(JsonElement value, uint maximumRate)
    {
        Assert.Empty(value.GetProperty("modes").EnumerateArray());
        Assert.Empty(value.GetProperty("dynamicTimingRanges").EnumerateArray());
        var range = Assert.Single(value.GetProperty("adaptiveSyncRanges").EnumerateArray());
        Assert.Equal(maximumRate, range.GetProperty("baseMaximumRefreshRateHz").GetUInt32());
        Assert.True(range.GetProperty("seamlessTransitionSupported").GetBoolean());
        Assert.Equal(maximumRate == 144, range.GetProperty("nativePanelRange").GetBoolean());
        Assert.Equal(maximumRate == 1024, range.GetProperty("adaptiveVTotalSupported").GetBoolean());
        var increase = range.GetProperty("frameDurationIncrease");
        Assert.Equal(maximumRate == 144, increase.GetProperty("zeroCodeMeansNoJitterThroughoutRange").GetBoolean());
        Assert.Equal(maximumRate == 144, range.GetProperty("frameDurationDecrease").GetProperty("zeroCodeMeansNoJitterThroughoutRange").GetBoolean());
        if (maximumRate == 144)
        {
            Assert.Equal(JsonValueKind.Null, increase.GetProperty("maximumSingleStepMicroseconds").ValueKind);
            Assert.False(increase.GetProperty("flickerRestrictedToSingleStep").GetBoolean());
        }
        else
        {
            Assert.Equal(250u, increase.GetProperty("maximumSingleStepMicroseconds").GetUInt32());
            Assert.True(increase.GetProperty("flickerRestrictedToSingleStep").GetBoolean());
        }
    }

    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
}
