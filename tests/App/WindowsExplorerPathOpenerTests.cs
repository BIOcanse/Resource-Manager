using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.LocalSystem;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsExplorerPathOpenerTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-open-path-{Guid.NewGuid():N}");

    [Fact]
    public void ExistingFileIsOpenedOnTheVerifiedUsersDesktop()
    {
        Directory.CreateDirectory(testRoot);
        var filePath = Path.Combine(testRoot, "selected file.txt");
        File.WriteAllText(filePath, "recorded");
        var broker = new Broker();
        var opener = new WindowsExplorerPathOpener(broker);

        var result = opener.Open(new(filePath), new(UserSid, 7));

        Assert.Equal("Select", result.Mode);
        var launch = Assert.Single(broker.Launches);
        Assert.Equal(7u, launch.SessionId);
        Assert.Equal(UserSid, launch.UserSid);
        Assert.EndsWith("ResourceManager.NativeUi.exe", launch.ExecutablePath);
        var shell = UserShellExecuteRequest.Decode(launch.Arguments[(UserShellExecuteRequest.Argument.Length + 1)..]);
        Assert.Equal("explorer.exe", shell.FileName);
        Assert.Equal($"/select,\"{filePath}\"", shell.Arguments);
    }

    [Fact]
    public void ChangedSessionUserCannotReceivePathAction()
    {
        Directory.CreateDirectory(testRoot);
        var broker = new Broker { UserSid = "S-1-5-21-4-5-6-1002" };
        var opener = new WindowsExplorerPathOpener(broker);

        Assert.Throws<UnauthorizedAccessException>(() => opener.Open(new(testRoot), new(UserSid, 7)));
        Assert.Empty(broker.Launches);
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class Broker : IInteractiveUserSessionBroker
    {
        internal string UserSid { get; init; } = WindowsExplorerPathOpenerTests.UserSid;
        internal List<(uint SessionId, string UserSid, string ExecutablePath, string Arguments)> Launches { get; } = [];

        public IReadOnlyList<uint> GetActiveSessionIds() => [7];
        public string? GetUserSid(uint sessionId) => UserSid;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string expectedExecutablePath)
            => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string executablePath, string arguments)
            => throw new NotSupportedException();
        public uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            Launches.Add((sessionId, expectedUserSid, executablePath, arguments));
            return 1;
        }
    }
}
