using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

internal static class DisplayIdCodeTimingTestData
{
    internal const string Name = "display_all_mode_capability";
    internal static byte[] Bytes() => Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAgAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAjqAGHE4LUBYLEUAAAAAAAAe8zmAGHE4LUBYLEUAAAAAAAAeAAAAEAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAABIQCAxMA5g2SgRCSgucIkYH///7/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwgIDMgD2IgITRAKEfwcXAVeAKwA3BCwAA4AEAPYiAn1DAoR/BxcBV4ArADcELAADgAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABWAgMZAOYjKQQAWADqKiCbfwc3BDtYAeINkwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK5wIAcCACMIBAQAWABMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
    internal static DeviceTopologyDisplayPath Path() => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes()));
    internal static DeviceTopologySnapshot Snapshot() => DisplayEdidModeTestData.Snapshot(Path());
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name = Name) => Assert.Single(
        Assert.Single(snapshot.Devices, d => d.ObjectType == "display_path").Fields, f => f.Definition.Name == name);
    internal static byte[] LegacyBytes() => Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAgAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAjqAGHE4LUBYLEUAAAAAAAAe8zmAGHE4LUBYLEUAAAAAAAAeAAAAEAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAABIQCAxMA5g2SgRCSgucIkYH///7/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwgIDMgD2IgITRAKEfwcXAVeAKwA3BCwAA4AEAPYiAn1DAoR/BxcBV4ArADcELAADgAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABWAgMZAOYjKQQAWADqKiCbfwc3BDtYAeINkwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK5wEgUCAAYBAgRYggAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
    internal static DeviceTopologyDisplayPath LegacyPath() => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(LegacyBytes()));
    internal static DeviceTopologySnapshot LegacySnapshot() => DisplayEdidModeTestData.Snapshot(LegacyPath());
    internal static void AssertValue(JsonElement value, int modeCount = 9) => AssertCodeTimingValue(value, false, modeCount);
    internal static void AssertLegacyValue(JsonElement value, int modeCount = 9, byte containerRevision = 0x12) => AssertCodeTimingValue(value, true, modeCount, containerRevision);
    private static void AssertCodeTimingValue(JsonElement value, bool legacy, int modeCount, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes"); Assert.Equal(modeCount, modes.GetArrayLength());
        Assert.Equal(3, value.GetProperty("selectionDeclarations").GetArrayLength());
        for (var i = 7; i < 9; i++)
        {
            var mode = modes[i]; Assert.Equal(legacy ? "displayid-iv-dmt-code" : "displayid-dmt-code", mode.GetProperty("encoding").GetString());
            Assert.Equal(4u, mode.GetProperty("blockIndex").GetUInt32()); Assert.Equal(i == 7 ? 8u : legacy ? 9u : 10u, mode.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(i == 7 ? 4u : 88u, mode.GetProperty("code").GetUInt32());
            Assert.Equal(i == 7 ? 640u : 4096u, mode.GetProperty("width").GetUInt32()); Assert.Equal(i == 7 ? 480u : 2160u, mode.GetProperty("height").GetUInt32());
            Assert.Equal(i != 7, mode.GetProperty("reducedBlanking").GetBoolean());
            Assert.Equal(JsonValueKind.Null, mode.GetProperty("timing").ValueKind);
            if (i == 7) Assert.Equal(60u, mode.GetProperty("nominalVerticalRateHz").GetUInt32()); else Assert.Equal(JsonValueKind.Null, mode.GetProperty("nominalVerticalRateHz").ValueKind);
            Assert.Equal(legacy ? new[] { i == 7 ? 4u : 88u } : new[] { i == 7 ? 4u : 88u, 0u }, mode.GetProperty("rawDescriptor").EnumerateArray().Select(x => x.GetUInt32()));
            Assert.Equal(legacy ? 1 : 0, mode.GetProperty("revision").GetInt32());
            Assert.Equal(legacy ? 1 : 8, mode.GetProperty("containerFlags").GetInt32());
            Assert.Equal(legacy ? containerRevision : 0x20, mode.GetProperty("containerRevision").GetInt32());
        }
        Assert.Equal(1978UL, modes[1].GetProperty("timing").GetProperty("frameRate").GetProperty("numerator").GetUInt64());
        Assert.Equal(6, modes[6].GetProperty("cvtParameters").GetProperty("deltaHorizontalBlankingCode").GetInt32());
    }
}
