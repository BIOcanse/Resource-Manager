using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareUninstallerCallerTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void QueuedUninstallCarriesOnlyServerSelectedUserIdentity()
    {
        var request = new SoftwareOperationRequest("windows-installed:owned", true);
        var caller = new SoftwareOperationCaller(UserSid, 7);
        var command = HostManagerOperationRequestCodec.SoftwareUninstall(request, caller);
        Assert.Equal(HostManagerOperationRequestSchemas.UserSoftwareUninstall, command.RequestSchemaId);
        var decoded = HostManagerOperationRequestCodec.DecodeUserSoftwareUninstall(command.CanonicalRequest);
        Assert.Equal(request, decoded.Request);
        Assert.Equal(caller, decoded.Caller);
        Assert.Throws<InvalidDataException>(() => HostManagerOperationRequestCodec.DecodeUserSoftwareUninstall(
            [.. command.CanonicalRequest, 0]));
    }

    [Fact]
    public void ComponentAndDependencyInstallerRequestsCarryTheServerSelectedUserIdentity()
    {
        var caller = new SoftwareOperationCaller(UserSid, 7);
        var component = HostManagerOperationRequestCodec.ComponentInstall(
            "pawnio", new ComponentActionRequest(true, "verified"), caller);
        var dependency = HostManagerOperationRequestCodec.DependencyInstaller(
            "pawnio", new OptionalDependencyInstallRequest(true, "verified"), caller);

        Assert.Equal(HostManagerOperationRequestSchemas.UserComponentInstall, component.RequestSchemaId);
        Assert.Equal(HostManagerOperationRequestSchemas.UserDependencyInstaller, dependency.RequestSchemaId);
        Assert.Equal(("pawnio", true, "verified", caller),
            HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(component.CanonicalRequest));
        Assert.Equal(("pawnio", true, "verified", caller),
            HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(dependency.CanonicalRequest));
    }

    [Fact]
    public async Task RegistryCommandIsDelegatedToFixedUserUiWithoutExpandingServiceEnvironment()
    {
        var broker = new Broker();
        var service = new SoftwareOperationManager(null!, new Inventory(), null!, broker);
        var result = await service.UninstallAsync(new("windows-installed:owned", true), new(UserSid, 7), default);
        Assert.Equal("UninstallerLaunchRequested", result.State);
        var launch = Assert.Single(broker.Launches);
        Assert.Equal(7u, launch.Session);
        Assert.Equal(UserSid, launch.Sid);
        Assert.EndsWith("ResourceManager.NativeUi.exe", launch.Executable);
        var encoded = launch.Arguments[(UserShellExecuteRequest.Argument.Length + 1)..];
        var shell = UserShellExecuteRequest.Decode(encoded);
        Assert.Equal(@"%LOCALAPPDATA%\Owned Software\uninstall.exe", shell.FileName);
        Assert.Equal("--owned-test", shell.Arguments);
    }

    [Fact]
    public async Task UnquotedExecutablePathWithSpacesIsKeptWholeWhenDelegatedToUserUi()
    {
        var broker = new Broker();
        var service = new SoftwareOperationManager(null!, new Inventory(@"C:\Program Files\Vendor Product\uninstall.exe /S"), null!, broker);

        _ = await service.UninstallAsync(new("windows-installed:owned", true), new(UserSid, 7), default);

        var launch = Assert.Single(broker.Launches);
        var encoded = launch.Arguments[(UserShellExecuteRequest.Argument.Length + 1)..];
        var shell = UserShellExecuteRequest.Decode(encoded);
        Assert.Equal(@"C:\Program Files\Vendor Product\uninstall.exe", shell.FileName);
        Assert.Equal("/S", shell.Arguments);
    }

    [Theory]
    [InlineData(null, 7u, true)]
    [InlineData(UserSid, 0u, true)]
    [InlineData(UserSid, 7u, false)]
    [InlineData("S-1-5-21-4-5-6-1002", 7u, true)]
    public async Task MissingOrChangedUserCannotLaunchWindowsUninstaller(string? sid, uint session, bool active)
    {
        var broker = new Broker { Active = active };
        var service = new SoftwareOperationManager(null!, new Inventory(), null!, broker);
        var caller = sid is null ? null : new SoftwareOperationCaller(sid, session);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UninstallAsync(
            new("windows-installed:owned", true), caller, default));
        Assert.Empty(broker.Launches);
    }

    [Fact]
    public void UserShellRequestRejectsOversizeAndDoesNotResolveThroughPathSearch()
    {
        Assert.Throws<InvalidDataException>(() => new UserShellExecuteRequest("owned.exe", new string('a', 24_000)).Encode());
        Assert.Throws<InvalidDataException>(() => UserShellExecuteRequest.Decode(new string('a', 24_001)));
        Assert.Throws<InvalidDataException>(() => UserShellProcessLauncher.ResolveExecutable(@"..\owned.exe"));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), UserShellProcessLauncher.ResolveExecutable("cmd.exe"));
        Assert.Equal("https://example.test/search?q=a", UserShellProcessLauncher.ResolveExecutable("https://example.test/search?q=a"));
        Assert.Equal(64, UserShellProcessLauncher.TryExecute([UserShellExecuteRequest.Argument]));
        Assert.Null(UserShellProcessLauncher.TryExecute(["--background-startup"]));

        var elevationRequest = new UserShellExecuteRequest("setup.exe", "/quiet", "runas");
        Assert.Equal(elevationRequest, UserShellExecuteRequest.Decode(elevationRequest.Encode()));
        Assert.Throws<InvalidDataException>(() => new UserShellExecuteRequest("setup.exe", "", "open").Encode());
    }

    [Fact]
    public void UserShellAllowsDirectoryTargetsOnlyForThePropertiesVerb()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rm-properties-{Guid.NewGuid():N}")).FullName;
        try
        {
            Assert.Equal(Path.GetFullPath(directory), UserShellProcessLauncher.ResolveExecutable(directory, allowDirectory: true));
            Assert.Throws<FileNotFoundException>(() => UserShellProcessLauncher.ResolveExecutable(directory));
            Assert.Equal("https://example.test/search?q=a", UserShellProcessLauncher.ResolveExecutable("https://example.test/search?q=a", allowDirectory: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ShellExecuteSuccessMayReturnNoProcessHandle()
    {
        Assert.Equal(0, UserShellProcessLauncher.ExecuteShellStart(static () => null));
    }

    private sealed class Inventory(string command = "\"%LOCALAPPDATA%\\Owned Software\\uninstall.exe\" --owned-test") : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstalledSoftwareEntry>>([
                new("windows-installed:owned", "Owned Software", null, null, null, [],
                    command, "CurrentUser")]);
    }

    private sealed class Broker : IInteractiveUserSessionBroker
    {
        internal bool Active { get; init; } = true;
        internal List<(uint Session, string Sid, string Executable, string Arguments)> Launches { get; } = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active ? [7u] : [];
        public string? GetUserSid(uint sessionId) => UserSid;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string executable) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string executable, string arguments) => throw new NotSupportedException();
        public uint LaunchUserShell(uint sessionId, string expectedUserSid, string executable, string arguments)
        { Launches.Add((sessionId, expectedUserSid, executable, arguments)); return 1; }
    }
}
