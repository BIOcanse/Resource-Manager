using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidFormulaProjectionTests
{
    [Theory]
     [InlineData(0x20)]
    public void FormulaDeclarationsSurviveOriginalOpaqueExportImportAndDomainProjection(byte version)
    {
        var path = DisplayEdidFormulaTestData.Path(version);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            static batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        Assert.Equal(path.EdidRawDeclaredBase64, roundtrip.EdidRawDeclaredBase64);
        var information = DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(roundtrip), DateTimeOffset.UtcNow);
        var field = DisplayEdidModeInformationCollectorTests.Field(information);
        Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, field.Definition.SourceId);
        DisplayEdidFormulaTestData.AssertValue(field.Value!.Value, version);
        Assert.Null(DisplayHdrInformationCollectorTests.Field(information, "display_all_resolution_actual").Value);
    }

    [Theory]
     [InlineData(0x20)]
    public void FormulaParameterChangesAloneChangeOriginalOpaqueHandleAndSemanticHash(byte version)
    {
        var first = DisplayEdidFormulaTestData.Path(version);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var comparer = new DeviceTopologySemanticComparer();
        var firstHash = comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(first));
        var firstHandle = catalog.AddDisplayPath(first);
        foreach (var option in new byte[] { 0x42, 0x12, 0x51 }) // fractional flag, stereo and RB version independently differ.
        {
            var bytes = DisplayEdidFormulaTestData.Bytes(version); bytes[148] = option;
            DisplayIdFormulaFixture.Repair(bytes);
            var changed = WindowsMonitorEdidReader.CreateReadResult(bytes).Capabilities!.ModeCapability!;
            var other = first with { Edid = first.Edid! with { ModeCapability = changed } };
            Assert.Equal(first.EdidRawDeclaredBase64, other.EdidRawDeclaredBase64);
            Assert.NotEqual(firstHandle, catalog.AddDisplayPath(other));
            Assert.NotEqual(firstHash, comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(other)));
        }
    }
}
