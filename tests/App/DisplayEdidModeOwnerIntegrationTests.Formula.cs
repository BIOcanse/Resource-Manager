using ResourceManager.ReadWrite;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class DisplayEdidModeOwnerIntegrationTests
{
    [Theory]
     [InlineData(0x20)]
    public async Task FormulaDirectCurrentFailureAndRecoveryUseTheOriginalOwner(byte version)
    {
        var phase = 0; var reads = 0;
        using var owner = Owner(() => {
            reads++; if (phase == 2) throw new IOException("recorded Type IX source failure");
            return DisplayEdidFormulaTestData.Snapshot(version, unsupported: phase == 1);
        });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var initialReads = reads;
        DisplayEdidFormulaTestData.AssertValue(backend.ReadCached(field).Value!.Structure!.Value, version);
        Assert.Equal(initialReads, reads);
        var direct = Assert.Single(await backend.CollectAsync([field], CancellationToken.None));
        DisplayEdidFormulaTestData.AssertValue(direct.Value!.Structure!.Value, version);
        var current = backend.ReadCached(field);
        Assert.Equal(direct.Field, current.Field); Assert.Equal(direct.ObservedAt, current.ObservedAt);
        Assert.Equal(direct.Value!.Structure!.Value.GetRawText(), current.Value!.Structure!.Value.GetRawText());
        phase = 1;
        var unsupported = await backend.CollectAsync([field, rawField], CancellationToken.None);
        Assert.Null(Assert.Single(unsupported, row => row.Field == field).Value);
        Assert.True(Assert.Single(unsupported, row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(DisplayEdidFormulaTestData.Bytes(version, unsupported: true)));
        phase = 2; Assert.Null(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value);
        phase = 0;
        DisplayEdidFormulaTestData.AssertValue(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value!.Structure!.Value, version);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field);
    }
}
