using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class FrontendSessionCredentialWorkerTests
{
    [Fact]
    public async Task SameWorkerSurvivesRejectedConnectionsAndReleasesRemovedSession()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sessions = new TestSessions(identity.User!.Value);
        var log = new WorkerLog();
        using var host = Host(sessions, log);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await host.StartAsync(deadline.Token);
        try
        {
            // No byte: a client closing during handshake must reject only this connection.
            using (var client = await ConnectAsync(sessions.Id, deadline.Token)) { }
            await log.Rejected.Reader.ReadAsync(deadline.Token);
            Assert.IsType<EndOfStreamException>(log.Errors.Last());

            await RejectAsync(255, sessions.Id, deadline.Token);
            await log.Rejected.Reader.ReadAsync(deadline.Token);
            Assert.IsType<InvalidDataException>(log.Errors.Last());

            // A real process cannot authenticate as the deliberately fictional session.
            await RejectAsync(FrontendSessionCredentialContract.ProtocolVersion, sessions.Id, deadline.Token);
            await log.Rejected.Reader.ReadAsync(deadline.Token);
            Assert.IsType<UnauthorizedAccessException>(log.Errors.Last());
            Assert.Empty(log.Warnings);
            Assert.False(host.ExecuteTask!.IsCompleted);

            sessions.Active = false;
            await AssertReleasedAsync(sessions, deadline.Token);
            Assert.False(host.ExecuteTask.IsCompleted);

            sessions.Active = true;
            await RejectAsync(255, sessions.Id, deadline.Token);
            await log.Rejected.Reader.ReadAsync(deadline.Token);
            Assert.Empty(log.Warnings);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(host.ExecuteTask!.IsCompletedSuccessfully);
        using var released = CreatePipe(sessions);
    }

    [Fact]
    public async Task StopCancelsAnIncompleteRequestAndReleasesItsPipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sessions = new TestSessions(identity.User!.Value);
        using var host = Host(sessions, new WorkerLog());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StartAsync(deadline.Token);
        try
        {
            using var client = await ConnectAsync(sessions.Id, deadline.Token);
            await host.StopAsync(deadline.Token);
            Assert.True(host.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(0, await client.ReadAsync(new byte[1], deadline.Token));
            using var released = CreatePipe(sessions);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static FrontendSessionCredentialHostedService Host(TestSessions sessions, WorkerLog log)
        => new(sessions, new LoopbackApiAccessToken(_ => throw new InvalidOperationException("No credential may be issued."),
            (_, _) => false), new HostManagerRuntimeIdentity(), log);

    private static async Task<NamedPipeClientStream> ConnectAsync(uint session, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", FrontendSessionCredentialContract.PipeName(session),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellationToken);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task RejectAsync(byte operation, uint session, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(session, cancellationToken);
        await client.WriteAsync(new byte[] { operation }, cancellationToken);
        await client.FlushAsync(cancellationToken);
        Assert.Equal(0, await client.ReadAsync(new byte[1], cancellationToken));
    }

    private static NamedPipeServerStream CreatePipe(TestSessions sessions)
        => WindowsLocalPipeFactory.Create(FrontendSessionCredentialContract.PipeName(sessions.Id),
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sessions.Sid})", 4096, 4096);

    private static async Task AssertReleasedAsync(TestSessions sessions, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { using var released = CreatePipe(sessions); return; }
            catch (System.ComponentModel.Win32Exception)
            { await Task.Delay(50, timeout.Token); }
        }
    }

    private sealed class TestSessions(string sid) : IInteractiveUserSessionBroker
    {
        // Test-only high session number; never a current desktop/session-0 pipe.
        public uint Id { get; } = (uint)Random.Shared.Next(1_000_000_000, int.MaxValue);
        public string Sid { get; } = sid;
        public volatile bool Active = true;
        public IReadOnlyList<uint> GetActiveSessionIds() => Active ? [Id] : [];
        public string? GetUserSid(uint sessionId) => sessionId == Id && Active ? Sid : null;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string args) => throw new NotSupportedException();
    }

    private sealed class WorkerLog : ILogger<FrontendSessionCredentialHostedService>
    {
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public ConcurrentQueue<Exception> Warnings { get; } = new();
        public System.Threading.Channels.Channel<bool> Rejected { get; }
            = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (error is null) return;
            if (level >= LogLevel.Warning) Warnings.Enqueue(error);
            else { Errors.Enqueue(error); Rejected.Writer.TryWrite(true); }
        }
    }
}
