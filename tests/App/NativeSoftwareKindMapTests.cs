using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSoftwareKindMapTests
{
    [Theory]
    [InlineData("game", SoftwareKinds.Game, 5U)]
    [InlineData("high_performance", SoftwareKinds.HighPerformance, 6U)]
    [InlineData("windows_system", SoftwareKinds.WindowsSystem, 8U)]
    public void NativeProjectionAcceptsExactKinds(
        string input,
        string canonical,
        uint expected)
    {
        Assert.Equal(expected, NativeSoftwareKindMap.ToNative(input));
        Assert.Equal(canonical, NativeSoftwareKindMap.ToManaged(expected));
    }

    [Theory]
    [InlineData("Game")]
    [InlineData("highperformance")]
    [InlineData("HighPerformance")]
    [InlineData("unknown")]
    public void NativeProjectionRejectsNonCanonicalKinds(string kind)
        => Assert.Throws<InvalidDataException>(() => NativeSoftwareKindMap.ToNative(kind));

    [Fact]
    public void RetiredDependencySupportNativeKindIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => NativeSoftwareKindMap.ToManaged(1));
    }
}
