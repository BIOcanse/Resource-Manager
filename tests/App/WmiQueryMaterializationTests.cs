using Wmi = ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace Resource_Manager_APP.Tests;

public sealed class WmiQueryMaterializationTests
{
    [Fact]
    public void OperatingSystemValuesRemainReadableAfterQueryResourcesClose()
    {
        var result = Wmi.QueryObjects(
            @"root\CIMV2",
            "SELECT Caption, Version FROM Win32_OperatingSystem",
            TimeSpan.FromSeconds(2));

        Assert.Null(result.Error);
        var row = Assert.Single(result.Rows);
        Assert.False(string.IsNullOrWhiteSpace(Wmi.ReadString(row, "Caption")));
        Assert.True(Version.TryParse(Wmi.ReadString(row, "Version"), out _));
    }

    [Fact]
    public void FailedQueryDoesNotPublishPartialRows()
    {
        var result = Wmi.QueryObjects(
            @"root\CIMV2",
            "SELECT Caption FROM ResourceManager_MissingWmiClass_ForQueryFailureTest",
            TimeSpan.FromSeconds(2));

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Rows);
    }
}
