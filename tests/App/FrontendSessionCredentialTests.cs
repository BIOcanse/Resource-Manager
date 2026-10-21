using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class FrontendSessionCredentialTests
{
    [Fact]
    public void CredentialsAreBoundToUiLifetimeRequesterAndServiceGeneration()
    {
        var live = true;
        var sameUser = true;
        var tokens = new LoopbackApiAccessToken(_ => live, (_, _) => sameUser);
        var owner = Owner();
        var token = tokens.Issue(owner);
        Assert.Equal(token, tokens.Issue(owner with { TransportConnectionId = 2 }));
        Assert.True(tokens.Validate(new DefaultHttpContext(), token));
        sameUser = false;
        Assert.False(tokens.Validate(new DefaultHttpContext(), token));
        sameUser = true;
        live = false;
        Assert.False(tokens.Validate(new DefaultHttpContext(), token));
        Assert.Throws<UnauthorizedAccessException>(() => tokens.Issue(owner));
        live = true;
        var next = new LoopbackApiAccessToken(_ => true, (_, _) => true);
        Assert.False(next.Validate(new DefaultHttpContext(), token));
        Assert.NotEqual(token, tokens.Issue(owner with { ProcessCreatedUtcTicks = owner.ProcessCreatedUtcTicks + 1 }));
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("session")]
    [InlineData("path")]
    public async Task ConnectedPipeRejectsIdentityMismatch(string mismatch)
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var name = "ResourceManager.Tests.Session." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name, $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})", 4096, 4096);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        await client.WriteAsync(new byte[] { 1 }, deadline.Token);
        await client.FlushAsync(deadline.Token);
        using var host = Host(new LoopbackApiAccessToken(_ => true, (_, _) => true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RespondAsync(server,
            mismatch == "session" ? current.SessionId + 1 : current.SessionId,
            mismatch == "sid" ? "S-1-5-18" : sid,
            mismatch == "path" ? Path.Combine(Path.GetTempPath(), "wrong.exe") : Environment.ProcessPath!, deadline.Token));
    }

    [Fact]
    public async Task ActualSessionPipeIssuesInMemoryCredentialToAttestedFrontend()
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var name = "ResourceManager.Tests.Session." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name, $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})", 4096, 4096);
        var tokens = new LoopbackApiAccessToken(_ => true, (_, _) => true);
        using var host = Host(tokens);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        var acquiring = FrontendSessionCredentialClient.AcquireAsync(Environment.ProcessPath!, deadline.Token, name);
        await accepting;
        await host.RespondAsync(server, current.SessionId, sid, Environment.ProcessPath!, deadline.Token);
        var token = await acquiring;
        Assert.NotNull(token);
        var context = new DefaultHttpContext();
        Assert.True(tokens.Validate(context, token));
        var owner = Assert.IsType<TrustedAdapterCallerIdentity>(context.Items[typeof(TrustedAdapterCallerIdentity)]);
        Assert.Equal(current.Id, owner.ProcessId);
        Assert.Equal(sid, owner.WindowsSid);
        Assert.Equal(current.StartTime.ToUniversalTime().Ticks, owner.ProcessCreatedUtcTicks);
    }

    [Fact]
    public async Task ClientRejectsPipeServerFromWrongProductPathBeforeSendingHandshake()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = "ResourceManager.Tests.Session." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        var acquiring = FrontendSessionCredentialClient.AcquireAsync(Path.Combine(Path.GetTempPath(), "wrong.exe"), deadline.Token, name);
        await accepting;
        Assert.Null(await acquiring);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public async Task ClientBoundsTheReplyBeforeAllocating(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => FrontendSessionCredentialContract.ReadAsync(stream, CancellationToken.None));
    }

    private static TrustedAdapterCallerIdentity Owner() => new(1, 1, 7, 10, "sid", 1, 1, 1, false,
        Path.GetFullPath("frontend.exe"), default, 1);
    private static FrontendSessionCredentialHostedService Host(LoopbackApiAccessToken tokens)
        => new(new UnusedSessions(), tokens, new HostManagerRuntimeIdentity(), NullLogger<FrontendSessionCredentialHostedService>.Instance);
    private sealed class UnusedSessions : IInteractiveUserSessionBroker
    {
        public IReadOnlyList<uint> GetActiveSessionIds() => throw new NotSupportedException();
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string args) => throw new NotSupportedException();
    }
}
