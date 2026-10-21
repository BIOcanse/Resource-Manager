using System.IO.Pipes;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.Security;

public sealed partial class FrontendSessionCredentialHostedService(IInteractiveUserSessionBroker sessions,
    LoopbackApiAccessToken tokens, HostManagerRuntimeIdentity runtime,
    ILogger<FrontendSessionCredentialHostedService> logger, IServiceProvider? services = null) : BackgroundService
{
    private readonly Dictionary<uint, Worker> workers = [];
    private long nextConnection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var active = sessions.GetActiveSessionIds().Where(id => id > 0).ToHashSet();
                    foreach (var id in workers.Keys.ToArray())
                    {
                        var worker = workers[id];
                        if (active.Contains(id) && !worker.Task.IsCompleted && sessions.GetUserSid(id) == worker.Sid) continue;
                        await worker.StopAsync();
                        workers.Remove(id);
                    }
                    foreach (var id in active)
                    {
                        if (workers.ContainsKey(id) || sessions.GetUserSid(id) is not { } sid) continue;
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        workers.Add(id, new(sid, stop, ServeSessionAsync(id, sid, stop.Token)));
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
                { logger.LogWarning(error, "Cannot prepare frontend session credentials."); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) await worker.StopAsync();
            workers.Clear();
        }
    }

    private async Task ServeSessionAsync(uint session, string sid, CancellationToken stoppingToken)
    {
        try
        {
            using var pipe = WindowsLocalPipeFactory.Create(FrontendSessionCredentialContract.PipeName(session),
                $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})", 4096, 4096);
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ServeConnectionAsync(pipe, checked((int)session), sid,
                    NativeUiLaunchHostedService.ResolveNativeUiPath(),
                    Path.Combine(AppContext.BaseDirectory, "ResourceManager.GpuLaunchBroker.exe"), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { logger.LogWarning(error, "Frontend session pipe is unavailable."); }
    }

    internal async Task ServeConnectionAsync(NamedPipeServerStream pipe, int expectedSession, string expectedSid,
        string expectedFrontend, string expectedBroker, CancellationToken stoppingToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await HandleSessionRequestAsync(pipe, expectedSession, expectedSid, expectedFrontend, expectedBroker, deadline.Token);
            // Both clients close after reading and validating the full reply. Disconnecting
            // immediately after WriteAsync can discard bytes they have not yet consumed.
            var end = new byte[1];
            if (await pipe.ReadAsync(end, deadline.Token) != 0)
                throw new InvalidDataException("Unexpected bytes after the user session request.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or ArgumentException)
        { logger.LogDebug(error, "User session request was rejected."); }
        // A peer closing early can leave PipeState.Broken with IsConnected=false.
        // This method owns an accepted connection; reset it in both states so the
        // session worker can accept the next client on the same pipe instance.
        finally { pipe.Disconnect(); }
    }

    internal async Task RespondAsync(NamedPipeServerStream pipe, int expectedSession, string expectedSid,
        string expectedExecutable, CancellationToken cancellationToken)
    {
        var version = new byte[1];
        await pipe.ReadExactlyAsync(version, cancellationToken);
        if (version[0] != FrontendSessionCredentialContract.ProtocolVersion)
            throw new InvalidDataException("Unsupported frontend session protocol.");
        await RespondToFrontendAsync(pipe, expectedSession, expectedSid, expectedExecutable, cancellationToken);
    }

    private async Task RespondToFrontendAsync(NamedPipeServerStream pipe, int expectedSession, string expectedSid,
        string expectedExecutable, CancellationToken cancellationToken)
    {
        var caller = AttestProductCaller(pipe, expectedSession, expectedSid, expectedExecutable);
        var token = tokens.Issue(caller);
        await FrontendSessionCredentialContract.WriteAsync(pipe, new(
            FrontendSessionCredentialContract.ProductId, FrontendSessionCredentialContract.ProtocolVersion,
            runtime.InstanceId, runtime.ProcessId, runtime.ProcessCreatedUtcTicks,
            caller.ProcessId, caller.ProcessCreatedUtcTicks, caller.WindowsSessionId, token), cancellationToken);
    }

    private ResourceManager.App.Domain.Adaptation.TrustedAdapterCallerIdentity AttestProductCaller(
        NamedPipeServerStream pipe, int expectedSession, string expectedSid, string expectedExecutable)
    {
        var caller = new WindowsNamedPipeCallerAttester().Attest(pipe, runtime.InstanceId,
            checked((ulong)Interlocked.Increment(ref nextConnection)));
        if (caller.WindowsSessionId != expectedSession || caller.WindowsSid != expectedSid
            || !StringComparer.OrdinalIgnoreCase.Equals(caller.CanonicalExecutablePath, Path.GetFullPath(expectedExecutable)))
            throw new UnauthorizedAccessException("The connected process is not the expected session product caller.");
        return caller;
    }

    private sealed record Worker(string Sid, CancellationTokenSource Stop, Task Task)
    {
        internal async Task StopAsync()
        {
            await Stop.CancelAsync();
            try { await Task; } catch (OperationCanceledException) { }
            Stop.Dispose();
        }
    }
}
