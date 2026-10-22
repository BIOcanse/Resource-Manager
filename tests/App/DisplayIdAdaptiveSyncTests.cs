using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdAdaptiveSyncTests
{
    [Theory]
    [MemberData(nameof(DisplayIdAdaptiveSyncTestData.PreparedCases), MemberType = typeof(DisplayIdAdaptiveSyncTestData))]
    public void IndependentDeclarationsAndWholeValueRejection(string name, string input,
        bool available, string expectedRanges)
    {
        var bytes = Convert.FromBase64String(input);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete, name);
        Assert.Equal(input, read.RawEdidDeclaredBase64);
        Assert.NotNull(read.Capabilities);
        if (!available) { Assert.Null(read.Capabilities.ModeCapability); return; }
        var value = Assert.IsType<EdidModeCapability>(read.Capabilities.ModeCapability);
        Assert.Empty(value.Modes); Assert.Empty(value.Ranges); Assert.Empty(value.DynamicTimingRanges);
        using var expected = JsonDocument.Parse(expectedRanges);
        Assert.Equal(expected.RootElement.GetArrayLength(), value.AdaptiveSyncRanges.Length);
        foreach (var pair in expected.RootElement.EnumerateArray().Zip(value.AdaptiveSyncRanges))
        {
            var e = pair.First; var a = pair.Second;
            Assert.Equal(e.GetProperty("blockIndex").GetUInt32(), a.BlockIndex);
            Assert.Equal(e.GetProperty("byteOffset").GetUInt32(), a.ByteOffset);
            Assert.Equal((byte)0x20, a.ContainerRevision); Assert.Equal((byte)0, a.ContainerFlags);
            Assert.Equal((byte)0, a.Revision);
            Assert.Equal(e.GetProperty("rawDescriptor").EnumerateArray().Select(v => v.GetByte()), a.RawDescriptor);
            Assert.Equal(e.GetProperty("baseMinimumRefreshHz").GetUInt32(), a.BaseMinimumRefreshRateHz);
            Assert.Equal(e.GetProperty("baseMaximumRefreshHz").GetUInt32(), a.BaseMaximumRefreshRateHz);
            Assert.Equal(e.GetProperty("nativePanelRange").GetBoolean(), a.NativePanelRange);
            Assert.Equal(e.GetProperty("adaptiveVTotalSupported").GetBoolean(), a.AdaptiveVTotalSupported);
            Assert.Equal(e.GetProperty("seamlessTransitionSupported").GetBoolean(), a.SeamlessTransitionSupported);
            VerifyTolerance(e.GetProperty("increase"), a.FrameDurationIncrease);
            VerifyTolerance(e.GetProperty("decrease"), a.FrameDurationDecrease);
        }
    }

    [Fact]
    public void JsonKeepsRawZeroSpecialAndOriginalConstructorRemainsEmpty()
    {
        var first = DisplayIdAdaptiveSyncTestData.PreparedCases().First();
        var value = EdidParser.Parse(Convert.FromBase64String((string)first[1]))!.ModeCapability!;
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(value))!;
        var range = Assert.Single(restored.AdaptiveSyncRanges);
        Assert.Equal(48u, range.BaseMinimumRefreshRateHz); Assert.Equal(144u, range.BaseMaximumRefreshRateHz);
        Assert.Null(range.FrameDurationIncrease.MaximumSingleStepMicroseconds);
        Assert.True(range.FrameDurationIncrease.ZeroCodeMeansNoJitterThroughoutRange);
        Assert.True(range.FrameDurationDecrease.ZeroCodeMeansNoJitterThroughoutRange);
        Assert.False(range.FrameDurationIncrease.FlickerRestrictedToSingleStep);
        Assert.Equal(value.AdaptiveSyncRanges[0].RawDescriptor.ToArray(), range.RawDescriptor.ToArray());
        Assert.Empty(new EdidModeCapability([], []).AdaptiveSyncRanges);
    }

    private static void VerifyTolerance(JsonElement expected, EdidAdaptiveSyncFrameDurationTolerance actual)
    {
        Assert.Equal(expected.GetProperty("flickerRestrictedToSingleStep").GetBoolean(), actual.FlickerRestrictedToSingleStep);
        var limit = expected.GetProperty("maximumSingleStepMicroseconds");
        Assert.Equal(limit.ValueKind == JsonValueKind.Null ? (uint?)null : limit.GetUInt32(), actual.MaximumSingleStepMicroseconds);
        Assert.Equal(expected.GetProperty("zeroCodeMeansNoJitterThroughoutRange").GetBoolean(), actual.ZeroCodeMeansNoJitterThroughoutRange);
    }
}
