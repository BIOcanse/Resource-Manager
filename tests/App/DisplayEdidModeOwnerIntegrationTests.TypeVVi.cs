using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed partial class DisplayEdidModeOwnerIntegrationTests
{
    [Fact]
    public async Task TypeVViRealParserDirectCurrentFailureAndRecoveryUseTheOriginalOwner()
    {
        var phase = 0;
        var reads = 0;
        using var owner = Owner(() =>
        {
            reads++;
            if (phase == 2) throw new IOException("recorded DisplayID V/VI read failure");
            return DisplayEdidTypeVViConsumerTestData.Snapshot(unknown: phase == 1);
        });
        var backend = owner.CreateBackend();
        Assert.Empty(backend.Catalog.Fields);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields,
            entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var initialReads = reads;
        DisplayEdidTypeVViConsumerTestData.AssertValue(backend.ReadCached(field).Value!.Structure!.Value);
        Assert.Equal(initialReads, reads);

        var direct = Assert.Single(await backend.CollectAsync([field], CancellationToken.None));
        DisplayEdidTypeVViConsumerTestData.AssertValue(direct.Value!.Structure!.Value);
        var current = backend.ReadCached(field);
        Assert.Equal(direct.Field, current.Field);
        Assert.Equal(direct.ObservedAt, current.ObservedAt);
        Assert.Equal(direct.Value!.Structure!.Value.GetRawText(), current.Value!.Structure!.Value.GetRawText());

        phase = 1;
        var unsupported = await backend.CollectAsync([field, rawField], CancellationToken.None);
        Assert.Null(Assert.Single(unsupported, row => row.Field == field).Value);
        Assert.True(Assert.Single(unsupported, row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(DisplayEdidTypeVViConsumerTestData.Bytes(unknown: true)));

        phase = 2;
        Assert.Null(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value);
        phase = 0;
        DisplayEdidTypeVViConsumerTestData.AssertValue(
            Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value!.Structure!.Value);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields,
            entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field);
    }
}

internal static class DisplayEdidTypeVViConsumerTestData
{
    internal static byte[] Bytes(bool unknown = false) => DisplayEdidTypeVViTestData.Bytes(unknown);

    internal static DeviceTopologySnapshot Snapshot(bool unknown = false)
        => DisplayEdidTypeVViTestData.Snapshot(unknown);

    internal static void AssertValue(JsonElement value) => DisplayEdidTypeVViTestData.AssertValue(value);
}
