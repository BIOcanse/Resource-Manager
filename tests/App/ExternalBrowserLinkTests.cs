using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class ExternalBrowserLinkTests
{
    [Theory]
    [InlineData("https://github.com/BIOcanse/Resource-Manager")]
    [InlineData("https://www.solidjs.com/")]
    [InlineData("http://example.com/")]
    public void UserLinkUsesDefaultBrowserWithoutElevation(string uri)
    {
        System.Diagnostics.ProcessStartInfo? actual = null;
        Assert.True(ExternalBrowserLink.TryOpen(uri, true, true, start => actual = start));
        Assert.NotNull(actual);
        Assert.True(actual.UseShellExecute);
        Assert.Equal(uri, actual.FileName);
        Assert.Empty(actual.Verb);
        Assert.Empty(actual.Arguments);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe", true, true)]
    [InlineData("ms-settings:", true, true)]
    [InlineData("javascript:alert(1)", true, true)]
    [InlineData("C:/Windows/notepad.exe", true, true)]
    [InlineData("https://github.com/", false, true)]
    [InlineData("https://github.com/", true, false)]
    public void OtherProtocolsAndUnrequestedPopupsDoNotLaunch(string uri, bool trusted, bool user)
        => Assert.False(ExternalBrowserLink.TryOpen(uri, trusted, user, _ => throw new Exception("Unexpected launch")));
}
