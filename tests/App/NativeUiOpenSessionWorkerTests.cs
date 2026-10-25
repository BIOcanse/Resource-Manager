using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiOpenSessionWorkerTests
{
    [Fact]
    public async Task OpenPipeSurvivesRejectedRequestsAndReleasesRemovedSession()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sessions = new TestSessions(identity.User!.Value);
        var log = new OpenSessionLog();
        using var host = Host(sessions, log);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await host.StartAsync(deadline.Token);
        try
        {
            using (var client = await ConnectAsync(sessions.Id, deadline.Token))
            {
            }
            await log.Rejected.Reader.ReadAsync(deadline.Token);

            await RejectAsync(255, sessions.Id, deadline.Token);
            await log.Rejected.Reader.ReadAsync(deadline.Token);

            await RejectAsync(NativeUiOpenSessionContract.OpenOperation,
                NativeUiOpenSessionContract.OpenForeground, sessions.Id, deadline.Token);
            await log.Rejected.Reader.ReadAsync(deadline.Token);
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
    public async Task StopCancelsAnIncompleteOpenRequestAndReleasesItsPipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sessions = new TestSessions(identity.User!.Value);
        using var host = Host(sessions, new OpenSessionLog());
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

    private static NativeUiOpenSessionHostedService Host(TestSessions sessions, OpenSessionLog log)
        => new(sessions, new HostManagerRuntimeIdentity(), log);

    private static async Task<NamedPipeClientStream> ConnectAsync(uint session, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", NativeUiOpenSessionContract.PipeName(session),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task RejectAsync(byte operation, uint session, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(session, cancellationToken);
        await AssertRejectedOrClosedAsync(client, [operation], cancellationToken);
    }

    private static async Task RejectAsync(byte operation, byte mode, uint session, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(session, cancellationToken);
        await AssertRejectedOrClosedAsync(client, [operation, mode], cancellationToken);
    }

    private static async Task AssertRejectedOrClosedAsync(
        NamedPipeClientStream client,
        byte[] request,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.WriteAsync(request, cancellationToken);
            await client.FlushAsync(cancellationToken);
            Assert.Equal(0, await client.ReadAsync(new byte[1], cancellationToken));
        }
        catch (IOException)
        {
            // This test deliberately connects with a fake WTS session ID and a non-Launcher
            // caller. The service may reject during peer attestation and close before reading
            // the operation bytes, so ERROR_BROKEN_PIPE is also a valid rejection result.
        }
    }

    private static NamedPipeServerStream CreatePipe(TestSessions sessions)
        => WindowsLocalPipeFactory.Create(NativeUiOpenSessionContract.PipeName(sessions.Id),
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sessions.Sid})", 4096, 4096);

    private static async Task AssertReleasedAsync(TestSessions sessions, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var released = CreatePipe(sessions);
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
    }

    private sealed class TestSessions(string sid) : IInteractiveUserSessionBroker
    {
        public uint Id { get; } = (uint)Random.Shared.Next(1_000_000_000, int.MaxValue);
        public string Sid { get; } = sid;
        public volatile bool Active = true;

        public IReadOnlyList<uint> GetActiveSessionIds() => Active ? [Id] : [];
        public string? GetUserSid(uint sessionId) => sessionId == Id && Active ? Sid : null;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string args) => throw new NotSupportedException();
    }

    private sealed class OpenSessionLog : ILogger<NativeUiOpenSessionHostedService>
    {
        public ConcurrentQueue<Exception> Warnings { get; } = new();
        public System.Threading.Channels.Channel<bool> Rejected { get; }
            = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning && error is not null) Warnings.Enqueue(error);
            if (level == LogLevel.Debug) Rejected.Writer.TryWrite(true);
        }
    }
}
