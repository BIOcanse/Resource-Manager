using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiBackendCredentialTests
{
    [Fact]
    public void ElevatedAdministratorPresentsNoKeyAndNeverOpensTheDefaultKey()
    {
        var opened = false;
        var reader = NativeUiBackendCredential.ForElevation(true, () => { opened = true; return () => "default-key"; });
        Assert.Equal(string.Empty, reader());
        Assert.False(opened);
    }

    [Fact]
    public void ProcessWithoutElevationPresentsTheDefaultKey()
    {
        var reader = NativeUiBackendCredential.ForElevation(false, () => () => "default-key");
        Assert.Equal("default-key", reader());
    }

    [Fact]
    public void CurrentProcessChoiceFollowsItsOwnToken()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "rm-credential-" + Guid.NewGuid().ToString("N"));
        var reader = NativeUiBackendCredential.ForCurrentProcess(packageRoot);
        // An unelevated runner gets the default-key reader, which is not invoked here (no key file exists).
        if (NativeUiBackendCredential.IsElevatedAdministrator()) Assert.Equal(string.Empty, reader());
        Assert.False(Directory.Exists(packageRoot));
    }
}
