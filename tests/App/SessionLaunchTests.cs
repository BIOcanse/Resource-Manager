using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class SessionLaunchTests
{
    private const string DesktopSid = "S-1-5-21-100-200-300-1001";
    private static TrustedAdapterCallerIdentity Caller => new(1, 1, 123, DateTime.UtcNow.Ticks,
        DesktopSid, 7, 1, 0x2000, false, Path.GetFullPath("arbitrary-client.exe"), new(1, 1, 1), 1);
    private static readonly SessionLaunchRequest Command = new(Path.GetFullPath("arbitrary-target.exe"), "--requested-action");

    [Fact]
    public void OrdinaryClientUsesOnlyItsAttestedDesktopSession()
    {
        var sessions = new Sessions();
        Assert.Equal(42u, SessionLaunchExecutor.Launch(sessions, Caller, false, Command));
        Assert.Equal((7u, DesktopSid, Command.ExecutablePath, Command.Arguments), Assert.Single(sessions.Launches));
    }

    [Fact]
    public void AlternateAdministratorSidUsesActualWtsDesktopSid()
    {
        var sessions = new Sessions();
        var caller = Caller with { WindowsSid = "S-1-5-21-100-200-300-1002", IsElevated = true, IntegrityLevelRid = 0x3000 };
        Assert.Equal(42u, SessionLaunchExecutor.Launch(sessions, caller, true, Command));
        Assert.Equal(DesktopSid, Assert.Single(sessions.Launches).Sid);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("session-zero")]
    [InlineData("unknown-owner")]
    [InlineData("alternate-user")]
    [InlineData("unverified-admin")]
    [InlineData("not-elevated")]
    [InlineData("medium-integrity")]
    public void InvalidDesktopEvidenceNeverDispatches(string reason)
    {
        var sessions = new Sessions();
        var caller = Caller;
        var administrator = true;
        if (reason == "inactive") sessions.Active = [];
        if (reason == "session-zero") caller = caller with { WindowsSessionId = 0 };
        if (reason == "unknown-owner") sessions.Sid = null;
        if (reason is "alternate-user" or "unverified-admin" or "not-elevated" or "medium-integrity")
            caller = caller with { WindowsSid = "S-1-5-21-100-200-300-1002", IsElevated = true, IntegrityLevelRid = 0x3000 };
        if (reason is "alternate-user" or "unverified-admin") administrator = false;
        if (reason == "not-elevated") caller = caller with { IsElevated = false };
        if (reason == "medium-integrity") caller = caller with { IntegrityLevelRid = 0x2000 };
        Assert.Throws<UnauthorizedAccessException>(() => SessionLaunchExecutor.Launch(sessions, caller, administrator, Command));
        Assert.Empty(sessions.Launches);
    }

    [Fact]
    public void SourceGeneratedPayloadCannotChooseASessionAndRetainsUnicode()
    {
        var command = new SessionLaunchRequest(Path.GetFullPath("软件.exe"), "--value 中文");
        Assert.Equal(command, SessionLaunchContract.Decode(SessionLaunchContract.Encode(command)));
        Assert.Equal(42u, SessionLaunchContract.DecodeResult(SessionLaunchContract.EncodeResult(42)).ProcessId);
        Assert.Throws<System.Text.Json.JsonException>(() => SessionLaunchContract.Decode(
            "{\"executablePath\":\"C:\\\\owned.exe\",\"arguments\":\"\",\"sessionId\":7}"));
        Assert.Throws<InvalidDataException>(() => SessionLaunchContract.Encode(new("relative.exe", "")));
        Assert.Throws<InvalidDataException>(() => SessionLaunchContract.Encode(command with { Arguments = new string('x', 24001) }));
        Assert.Throws<InvalidDataException>(() => SessionLaunchContract.Encode(command with { Arguments = "x\0y" }));
    }

    private sealed class Sessions : IInteractiveUserSessionBroker
    {
        internal IReadOnlyList<uint> Active = [7];
        internal string? Sid = DesktopSid;
        internal List<(uint Session, string Sid, string Path, string Arguments)> Launches = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active;
        public string? GetUserSid(uint sessionId) => Sid;
        public uint LaunchDesktopProcess(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            Launches.Add((sessionId, expectedUserSid, executablePath, arguments));
            return 42;
        }
    }
}
