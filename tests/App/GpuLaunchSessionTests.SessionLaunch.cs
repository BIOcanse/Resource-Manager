using System.Diagnostics;
using System.Security.Principal;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuLaunchSessionTests
{
    [Fact]
    public async Task SessionLaunchUsesRealPipeCallerSessionAndSessionScopeWithoutImageBinding()
    {
        using var own = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(own.SessionId > 0);
        var fixture = TrustedAccessTestFactory.Create();
        var sessions = new SessionLaunchDispatch(checked((uint)own.SessionId), identity.User!.Value);
        using var host = CreateHost(fixture.Owner, new LaunchServices(), sessions);
        var command = new SessionLaunchRequest(Path.GetFullPath("third-party-target.exe"), "--independent-action");
        var payload = SessionLaunchContract.Encode(command);
        var response = await InvokeAsync(host, TrustedAccessInvocationContract.SessionLaunchOperation, fixture.KeyFile.Value!, payload);
        Assert.Equal(TrustedAccessInvocationContract.Success, response.Status);
        Assert.Equal(73u, SessionLaunchContract.DecodeResult(response.Payload!).ProcessId);
        Assert.Equal((checked((uint)own.SessionId), identity.User.Value, command.ExecutablePath, command.Arguments),
            Assert.Single(sessions.Launches));

        var readOnly = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest("read", AccessKeyPurposes.Control,
            [AccessKeyScopes.Read]), CancellationToken.None);
        var denied = await InvokeAsync(host, TrustedAccessInvocationContract.SessionLaunchOperation, readOnly.ApiKey, payload);
        Assert.Equal(TrustedAccessInvocationContract.Forbidden, denied.Status);
        var invalidKey = await InvokeAsync(host, TrustedAccessInvocationContract.SessionLaunchOperation,
            "rm_sk_" + new string('0', 64), payload);
        Assert.Equal(TrustedAccessInvocationContract.Unauthorized, invalidKey.Status);
        Assert.Single(sessions.Launches);

        var injectedSession = await InvokeAsync(host, TrustedAccessInvocationContract.SessionLaunchOperation,
            fixture.KeyFile.Value!, payload[..^1] + ",\"sessionId\":999}");
        Assert.Equal(TrustedAccessInvocationContract.InvalidRequest, injectedSession.Status);
        sessions.Active = [];
        var inactive = await InvokeAsync(host, TrustedAccessInvocationContract.SessionLaunchOperation, fixture.KeyFile.Value!, payload);
        Assert.Equal(TrustedAccessInvocationContract.Forbidden, inactive.Status);
        Assert.Single(sessions.Launches);
    }

    private sealed class SessionLaunchDispatch(uint session, string sid) : IInteractiveUserSessionBroker
    {
        internal IReadOnlyList<uint> Active = [session];
        internal List<(uint Session, string Sid, string Path, string Arguments)> Launches = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active;
        public string? GetUserSid(uint sessionId) => sessionId == session ? sid : null;
        public uint LaunchDesktopProcess(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            Launches.Add((sessionId, expectedUserSid, executablePath, arguments));
            return 73;
        }
    }
}
