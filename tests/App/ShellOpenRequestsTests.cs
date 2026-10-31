using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

// docs/crash_dumps_2026-10-31.md: the window process opens the prefilled GitHub issue page and selects the report package
// when the user clicks; it accepts only https://github.com/ addresses and fully qualified local file paths.
public sealed class ShellOpenRequestsTests
{
    [Theory]
    [InlineData("https://github.com/BIOcanse/Resource-Manager/issues/new?title=a&body=b", true)]
    [InlineData("https://GitHub.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://gist.github.com/x", false)]
    [InlineData("https://user@github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("github.com/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpsGitHubAddressesOpen(string? address, bool accepted)
        => Assert.Equal(accepted, ShellOpenRequests.TryGitHubAddress(address, out _));

    [Theory]
    [InlineData(@"C:\ProgramData\RM\UserData\CrashReports\packages\a.zip", true)]
    [InlineData(@"relative\a.dmp", false)]
    [InlineData(@"C:a.dmp", false)]
    [InlineData(@"\\server\share\a.dmp", false)]
    [InlineData(@"\\?\C:\a.dmp", false)]
    [InlineData(@"C:\dumps\*.dmp", false)]
    [InlineData("C:\\dumps\\a\".dmp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFullyQualifiedLocalPathsAreRevealed(string? path, bool accepted)
        => Assert.Equal(accepted, ShellOpenRequests.TryLocalFilePath(path, out _));

    [Fact]
    public void RevealSelectsTheFileWithExplorerFromTheWindowsDirectory()
    {
        var start = ShellOpenRequests.RevealStart(@"C:\Program Files\RM\crash-dumps\a b.dmp");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), start.FileName);
        Assert.Equal(@"/select,""C:\Program Files\RM\crash-dumps\a b.dmp""", start.Arguments);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void BrowserOpensTheValidatedAddressThroughTheShell()
    {
        Assert.True(ShellOpenRequests.TryGitHubAddress("https://github.com/BIOcanse/Resource-Manager/issues/new?title=x", out var uri));
        var start = ShellOpenRequests.BrowserStart(uri);
        Assert.Equal("https://github.com/BIOcanse/Resource-Manager/issues/new?title=x", start.FileName);
        Assert.True(start.UseShellExecute);
    }
}
