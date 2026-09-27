using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.6-beta.20260926.4", "0.2.6", -1)]
    [InlineData("0.2.6-beta.2", "0.2.6-beta.10", -1)]
    [InlineData("0.2.6", "0.3.0-beta.1", -1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    public void CompareTo_OrdersReleaseAndPrerelease(string left, string right, int expected)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var parsedLeft));
        Assert.True(ReleaseVersion.TryParse(right, out var parsedRight));
        Assert.Equal(expected, Math.Sign(parsedLeft!.CompareTo(parsedRight)));
    }

    [Fact]
    public void UnknownVendorFormatCannotAuthorizeUpgrade()
    {
        Assert.False(ReleaseVersion.TryParse("2026 R2 custom", out _));
        Assert.True(ReleaseVersion.TryParse("0.2.6-beta.4", out var version));
        Assert.Equal("0.2", version!.Series);
        Assert.False(version.Stable);
    }
}
