using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSoftwareKindMapTests
{
    [Theory]
    [InlineData("game", SoftwareKinds.Game, 5U)]
    [InlineData("highperformance", SoftwareKinds.HighPerformance, 6U)]
    [InlineData("windowssystem", SoftwareKinds.WindowsSystem, 8U)]
    public void NativeProjectionAcceptsCanonicalKindsWithoutCaseSensitivity(
        string input,
        string canonical,
        uint expected)
    {
        Assert.Equal(expected, NativeSoftwareKindMap.ToNative(input));
        Assert.Equal(canonical, NativeSoftwareKindMap.ToManaged(expected));
    }

    [Fact]
    public void RetiredDependencySupportNativeKindIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => NativeSoftwareKindMap.ToManaged(1));
    }
}
