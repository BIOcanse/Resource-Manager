using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdIntegratedStageTests
{
    [Fact]
    public void LegacyTypeVViAndModernAdaptiveSyncCoexistInOneModeCapability()
    {
        var legacy = DisplayEdidTypeVViTestData.Bytes();
        var modern = Convert.FromBase64String((string)DisplayIdAdaptiveSyncTestData.PreparedCases().First()[1]);
        Assert.Equal(384, legacy.Length);
        Assert.Equal(256, modern.Length);

        var bytes = new byte[legacy.Length + 128];
        legacy.CopyTo(bytes, 0);
        modern.AsSpan(128, 128).CopyTo(bytes.AsSpan(legacy.Length));
        bytes[126] = 3;
        DisplayEdidTypeVViTestData.RepairChecksums(bytes);

        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete);
        Assert.NotNull(read.RawEdidDeclaredBase64);
        var capability = Assert.IsType<EdidModeCapability>(read.Capabilities!.ModeCapability);
        Assert.Equal(new[] { "displayid-v", "displayid-vi" }, capability.Modes.Select(static mode => mode.Encoding));
        Assert.Equal(1920u, capability.Modes[0].Width!.Value);
        Assert.Equal(1920u, capability.Modes[1].Width!.Value);
        Assert.Single(capability.AdaptiveSyncRanges);
        Assert.Equal((48u, 144u),
            (capability.AdaptiveSyncRanges[0].BaseMinimumRefreshRateHz,
                capability.AdaptiveSyncRanges[0].BaseMaximumRefreshRateHz));
    }
}
