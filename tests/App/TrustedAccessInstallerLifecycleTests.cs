using ResourceManager.Installer;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessInstallerLifecycleTests
{
    [Fact]
    public void FreshInstallInitializesKeysBeforeRegisteringTheBackendService()
    {
        var calls = new List<string>();
        const string backend = @"C:\Program Files\ResourceManager\Bin\ResourceManager\ResourceManager.exe";

        TrustedAccessInstallLifecycle.InitializeBeforeServiceRegistration(
            backend,
            (path, argument) =>
            {
                calls.Add($"initialize:{path}:{argument}");
                return 0;
            },
            () => calls.Add("register-service"));

        Assert.Equal(
            [$"initialize:{backend}:--initialize-trusted-access", "register-service"],
            calls);
    }

    [Fact]
    public void FailedKeyInitializationPreventsServiceRegistration()
    {
        var serviceRegistered = false;

        var error = Assert.Throws<IOException>(() => TrustedAccessInstallLifecycle.InitializeBeforeServiceRegistration(
            @"C:\Program Files\ResourceManager\Bin\ResourceManager\ResourceManager.exe",
            static (_, argument) =>
            {
                Assert.Equal("--initialize-trusted-access", argument);
                return 13;
            },
            () => serviceRegistered = true));

        Assert.Contains("(13)", error.Message, StringComparison.Ordinal);
        Assert.False(serviceRegistered);
    }
}
