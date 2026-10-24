using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareKindsTests
{
    [Theory]
    [InlineData("Adapted", "adapted")]
    [InlineData("Controlled", "controlled")]
    [InlineData("Managed", "managed")]
    [InlineData("Game", "game")]
    [InlineData("HighPerformance", "high_performance")]
    [InlineData("highperformance", "high_performance")]
    [InlineData("Other", "other")]
    [InlineData("WindowsSystem", "windows_system")]
    [InlineData("WindowsComponent", "windows_component")]
    [InlineData("WindowsService", "windows_service")]
    [InlineData("RuntimePackage", "runtime_package")]
    [InlineData("RuntimeProduct", "runtime_product")]
    [InlineData("RuntimeRoot", "runtime_root")]
    [InlineData("Unattributed", "unattributed")]
    public void StoredLegacyKindsReadAsTheSingleCurrentSpelling(string stored, string current)
    {
        Assert.Equal(current, SoftwareKinds.ReadStored(stored));
        Assert.Equal(current, SoftwareKinds.ReadStored(current));
        Assert.True(SoftwareKinds.IsKnown(current));
        Assert.False(SoftwareKinds.IsKnown(stored));
        Assert.Equal(current, NativeSoftwareKindMap.ToManaged(NativeSoftwareKindMap.ToNative(current)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("游戏")]
    [InlineData(" game ")]
    public void UnknownStoredKindFailsLoudly(string? value)
    {
        Assert.False(SoftwareKinds.IsKnown(value));
        Assert.Throws<InvalidDataException>(() => SoftwareKinds.ReadStored(value));
    }

    [Fact]
    public void SystemResidualIsADisplayKindOnly()
    {
        Assert.Equal("system_residual", SoftwareDisplayKinds.SystemResidual);
        Assert.False(SoftwareKinds.IsKnown(SoftwareDisplayKinds.SystemResidual));
        Assert.Throws<InvalidDataException>(() => SoftwareKinds.ReadStored(SoftwareDisplayKinds.SystemResidual));
    }
}
